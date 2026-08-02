using FluentAssertions;
using Hannibal;
using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkerRClone.Services;
using WorkerRClone.Services.Providers.OAuth;
using WorkerRClone.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace WorkerRClone.Tests.Services.Providers.OAuth;

/// <summary>
/// Opt-in, read-only check against the real OneDrive account behind a configured
/// <see cref="Storage"/> row.
///
/// <para><b>Skipped by default.</b> It only runs when <c>BACKER_LIVE_OAUTH_TEST=1</c>,
/// <c>BACKER_LIVE_STORAGE_ID</c> and a Postgres connection string
/// (<c>BACKER_LIVE_DB_CONNECTION</c>, falling back to <c>HANNIBAL_DB_CONNECTION</c>)
/// are all set - see <see cref="LiveOAuthFactAttribute"/>.</para>
///
/// <para>What it does when enabled:</para>
/// <list type="number">
///   <item>reads the Storage row (no tracking, no modification),</item>
///   <item>runs <c>EnsureTokensValidAsync</c> - the exact production code path, which
///         refreshes the access token only when it is within 5 minutes of expiry,</item>
///   <item>performs a single read-only <c>GET https://graph.microsoft.com/v1.0/me/drive</c>,</item>
///   <item>reports token validity, expiry and the resolved drive id/type.</item>
/// </list>
///
/// <para><b>What it never does:</b> start rclone, write to the remote, or touch any
/// column of any table other than the three token columns of the one storage row -
/// and those only when Microsoft actually rotated the tokens, which is exactly what
/// production persists after a refresh. Microsoft rotates refresh tokens, so NOT
/// persisting a rotated token would break the next production run; set
/// <c>BACKER_LIVE_PERSIST_TOKENS=0</c> to suppress the write-back if you understand
/// that consequence.</para>
/// </summary>
public class LiveOneDriveCredentialCheckTests
{
    public const string ClientIdVariable = "BACKER_LIVE_ONEDRIVE_CLIENT_ID";
    public const string ClientSecretVariable = "BACKER_LIVE_ONEDRIVE_CLIENT_SECRET";
    public const string PersistTokensVariable = "BACKER_LIVE_PERSIST_TOKENS";

    private readonly ITestOutputHelper _output;

    public LiveOneDriveCredentialCheckTests(ITestOutputHelper output) => _output = output;

    [LiveOAuthFact]
    public async Task LiveOneDriveCredentials_AreValid_AndDriveIsReadable()
    {
        var cancellationToken = TestContext();

        var storageId = LiveOAuthFactAttribute.StorageId;
        var connectionString = LiveOAuthFactAttribute.ConnectionString!;

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new TestOutputLoggerProvider(_output));
        });

        var contextOptions = new DbContextOptionsBuilder<HannibalContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var context = new HannibalContext(
            contextOptions, loggerFactory.CreateLogger<HannibalContext>());

        var persisted = await context.Storages
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == storageId, cancellationToken);

        persisted.Should().NotBeNull(
            $"storage id {storageId} (from {LiveOAuthFactAttribute.StorageIdVariable}) must exist in the database");
        persisted!.Technology.Should().Be(
            "onedrive",
            "the live check only knows how to talk to Microsoft Graph");

        _output.WriteLine($"Storage #{persisted.Id} '{persisted.UriSchema}' technology='{persisted.Technology}'");
        _output.WriteLine($"  access token present : {!string.IsNullOrEmpty(persisted.AccessToken)}");
        _output.WriteLine($"  refresh token present: {!string.IsNullOrEmpty(persisted.RefreshToken)}");
        _output.WriteLine($"  expires at (UTC)     : {persisted.ExpiresAt.ToUniversalTime():O}");

        // Work on a copy so nothing can accidentally be written back through EF.
        var state = new StorageState { Storage = new Storage(persisted) };

        var oauthOptions = new OAuthOptions();
        oauthOptions.Providers["onedrive"] = new OAuthProviderOptions
        {
            ClientId = ResolveClientId(persisted),
            ClientSecret = ResolveClientSecret(persisted)
        };

        var oauth2ClientFactory = new OAuth2ClientFactory(oauthOptions);
        state.OAuthClient = oauth2ClientFactory.CreateOAuth2Client(Guid.Empty, "onedrive");

        // An empty provider scope: the base class' UpdateStorageInDatabaseAsync resolves
        // IHannibalServiceClient from it. There is deliberately no API client here - the
        // harness persists the (possibly rotated) tokens itself, further below.
        await using var emptyProvider = new ServiceCollection().BuildServiceProvider();

        var provider = new OneDriveProvider(
            loggerFactory.CreateLogger<OneDriveProvider>(),
            oauth2ClientFactory,
            emptyProvider.GetRequiredService<IServiceScopeFactory>());

        var validation = await provider.EnsureTokensValidAsync(
            state, cancellationToken: cancellationToken);

        _output.WriteLine(
            $"EnsureTokensValidAsync -> wasValid={validation.WasValid} " +
            $"wasRefreshed={validation.WasRefreshed} isNowValid={validation.IsNowValid} " +
            $"error='{validation.ErrorMessage}'");

        await PersistRotatedTokensAsync(context, persisted, state.Storage, cancellationToken);

        validation.IsNowValid.Should().BeTrue(
            $"the OneDrive tokens must still be usable, but validation reported: {validation.ErrorMessage}");

        // Read-only GET https://graph.microsoft.com/v1.0/me/drive - no writes, no rclone.
        var (driveId, driveType) = await provider.GetDriveInfoAsync(
            state.Storage.AccessToken, cancellationToken);

        _output.WriteLine($"Microsoft Graph /v1.0/me/drive -> driveId='{driveId}' driveType='{driveType}'");
        _output.WriteLine($"New expiry (UTC): {state.Storage.ExpiresAt.ToUniversalTime():O}");
        _output.WriteLine("LIVE CHECK PASSED: the OneDrive credentials are valid and the drive is readable.");

        driveId.Should().NotBeNullOrWhiteSpace();
        driveType.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Persist a rotated access/refresh token, exactly as the production refresh path does.
    /// Only the three token columns (and UpdatedAt) of this one row are touched, and only
    /// when Microsoft actually handed out new values.
    /// </summary>
    private async Task PersistRotatedTokensAsync(
        HannibalContext context,
        Storage original,
        Storage refreshed,
        CancellationToken cancellationToken)
    {
        var changed =
            original.AccessToken != refreshed.AccessToken ||
            original.RefreshToken != refreshed.RefreshToken ||
            original.ExpiresAt != refreshed.ExpiresAt;

        if (!changed)
        {
            _output.WriteLine("Tokens unchanged - nothing written to the database.");
            return;
        }

        if (Environment.GetEnvironmentVariable(PersistTokensVariable) == "0")
        {
            _output.WriteLine(
                $"WARNING: tokens were rotated but {PersistTokensVariable}=0, so they are NOT persisted. " +
                "Microsoft rotates refresh tokens - the next production refresh may fail.");
            return;
        }

        var row = await context.Storages.FirstAsync(s => s.Id == original.Id, cancellationToken);
        row.AccessToken = refreshed.AccessToken;
        row.RefreshToken = refreshed.RefreshToken;
        row.ExpiresAt = refreshed.ExpiresAt;
        row.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        _output.WriteLine(
            "Rotated tokens written back to the Storage row (same columns production writes after a refresh).");
    }

    private static string ResolveClientId(Storage storage)
    {
        var fromEnv = Environment.GetEnvironmentVariable(ClientIdVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        if (string.IsNullOrWhiteSpace(storage.ClientId))
        {
            throw new InvalidOperationException(
                $"No OAuth2 client id available: set {ClientIdVariable} or fill the storage row's ClientId.");
        }

        return storage.ClientId.Trim();
    }

    private static string ResolveClientSecret(Storage storage)
    {
        var fromEnv = Environment.GetEnvironmentVariable(ClientSecretVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        if (string.IsNullOrWhiteSpace(storage.ClientSecret))
        {
            throw new InvalidOperationException(
                $"No OAuth2 client secret available: set {ClientSecretVariable} or fill the storage row's ClientSecret. " +
                "An expired client secret is the exact failure this check exists to surface.");
        }

        return storage.ClientSecret.Trim();
    }

    private static CancellationToken TestContext()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return cts.Token;
    }

    /// <summary>
    /// Guards the gate itself: with <c>BACKER_LIVE_OAUTH_TEST</c> unset the live check
    /// must be skipped, and the reason must say how to enable it.
    /// </summary>
    [Fact]
    public void LiveCheck_IsSkipped_UnlessExplicitlyEnabled()
    {
        var enabled = Environment.GetEnvironmentVariable(LiveOAuthFactAttribute.EnableVariable) == "1";
        var skipReason = LiveOAuthFactAttribute.ComputeSkipReason();

        if (enabled)
        {
            _output.WriteLine($"{LiveOAuthFactAttribute.EnableVariable}=1 - live check opted in.");
            return;
        }

        skipReason.Should().NotBeNull();
        skipReason.Should().Contain(LiveOAuthFactAttribute.EnableVariable);
        skipReason.Should().Contain(LiveOAuthFactAttribute.StorageIdVariable);
        _output.WriteLine($"Live check skip reason: {skipReason}");
    }
}
