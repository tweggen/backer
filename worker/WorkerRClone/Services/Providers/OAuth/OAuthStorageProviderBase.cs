using System.Text.Json;
using Hannibal;
using Hannibal.Client;
using Hannibal.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers.OAuth;

/// <summary>
/// Base class for OAuth-based storage providers (Dropbox, OneDrive, Google Drive, etc.)
/// </summary>
public abstract class OAuthStorageProviderBase : StorageProviderBase
{
    protected readonly OAuth2ClientFactory OAuth2ClientFactory;
    protected readonly IServiceScopeFactory ServiceScopeFactory;

    public override bool RequiresOAuth => true;

    protected OAuthStorageProviderBase(
        ILogger logger,
        OAuth2ClientFactory oauth2ClientFactory,
        IServiceScopeFactory serviceScopeFactory) 
        : base(logger)
    {
        OAuth2ClientFactory = oauth2ClientFactory;
        ServiceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    public override async Task InitializeAsync(StorageState state, CancellationToken cancellationToken)
    {
        state.OAuthClient = OAuth2ClientFactory.CreateOAuth2Client(new Guid(), Technology);
        await RefreshTokensAsync(state, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task RefreshTokensAsync(StorageState state, CancellationToken cancellationToken)
    {
        var storage = state.Storage;
        
        if (string.IsNullOrWhiteSpace(storage.AccessToken) && 
            string.IsNullOrWhiteSpace(storage.RefreshToken))
        {
            Logger.LogInformation($"{Technology}: Skipping token refresh, no login happened yet.");
            return;
        }

        var oldAccessToken = storage.AccessToken;
        var oldRefreshToken = storage.RefreshToken;
        var oldExpiresAt = storage.ExpiresAt;

        var newAccessToken = await state.OAuthClient!.GetCurrentTokenAsync(
            storage.RefreshToken, false, cancellationToken);

        if (string.IsNullOrEmpty(newAccessToken))
        {
            throw new UnauthorizedAccessException($"No access token found for {Technology}.");
        }

        bool tokensChanged = false;

        if (oldAccessToken != newAccessToken)
        {
            Logger.LogInformation($"Access token refreshed for storage {storage.UriSchema}");
            storage.AccessToken = newAccessToken;
            tokensChanged = true;
        }

        var newRefreshToken = state.OAuthClient.RefreshToken;
        if (!string.IsNullOrEmpty(newRefreshToken) && oldRefreshToken != newRefreshToken)
        {
            Logger.LogInformation($"Refresh token updated for storage {storage.UriSchema}");
            storage.RefreshToken = newRefreshToken;
            tokensChanged = true;
        }

        var newExpiresAt = state.OAuthClient.ExpiresAt;
        if (newExpiresAt != default && storage.ExpiresAt != newExpiresAt)
        {
            storage.ExpiresAt = newExpiresAt;
            tokensChanged = true;
        }

        Logger.LogDebug($"Storage {storage.UriSchema} oldRefreshToken = {oldRefreshToken}, oldExpiresAt = {oldExpiresAt} newRefreshToken = {newRefreshToken}, newExpiresAt = {newExpiresAt}");
        
        if (tokensChanged)
        {
            await UpdateStorageInDatabaseAsync(storage, cancellationToken);
        }
    }

    /// <summary>
    /// Update storage tokens in the database
    /// </summary>
    protected async Task UpdateStorageInDatabaseAsync(Storage storage, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = ServiceScopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
            await client.UpdateStorageAsync(storage.Id, storage, cancellationToken);
            Logger.LogInformation($"Updated storage {storage.UriSchema} tokens in database");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update storage in database");
        }
    }

    /// <summary>
    /// Build the standard OAuth token JSON for rclone
    /// </summary>
    protected string BuildTokenJson(Storage storage)
    {
        var tokenObject = new
        {
            access_token = storage.AccessToken,
            refresh_token = storage.RefreshToken,
            token_type = "bearer",
            expiry = storage.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        };
        return JsonSerializer.Serialize(tokenObject);
    }

    /// <inheritdoc />
    public override ValidationResult Validate(Storage storage)
    {
        var baseResult = base.Validate(storage);
        if (!baseResult.IsValid) return baseResult;

        if (string.IsNullOrWhiteSpace(storage.ClientId))
            return ValidationResult.Failure("ClientId is required for OAuth providers");
        if (string.IsNullOrWhiteSpace(storage.ClientSecret))
            return ValidationResult.Failure("ClientSecret is required for OAuth providers");

        return ValidationResult.Success();
    }
}
