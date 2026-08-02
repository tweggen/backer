using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.IntegrationTests.TestSupport;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate C acceptance 2 - regression proof for Phase 1 fix 4.
///
/// <c>HannibalService.ProcessOAuth2ResultAsync</c> writes the freshly obtained
/// tokens onto the <see cref="Storage"/> entity it just read from its own
/// <c>HannibalContext</c> and then calls
/// <c>UpdateStorageAsync(sto.Id, sto, ...)</c>. Because EF Core's identity map
/// hands back the very same instance, <c>UpdateStorageAsync</c>'s old-vs-new
/// comparisons compare the object with itself and can never see a change. Only
/// the <c>isSelfUpdate</c> guard
/// (<c>HannibalServiceStorages.cs:96</c> and <c>:130-138</c>) makes the
/// <c>StorageReauthenticated</c> broadcast happen at all.
///
/// Remove that guard and this test fails with zero broadcasts.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StorageReauthenticatedBroadcastTests : ApiIntegrationTestBase
{
    private const string Route = "/api/hannibal/v1/users/processOAuth2Result";

    private const string OAuth2Email = "reauth-user@example.com";
    private const string UriSchema = "RegressionOneDrive";

    private const string TokenResponse =
        """
        {"token_type":"Bearer","scope":"Files.ReadWrite User.Read offline_access","expires_in":3600,"access_token":"fresh-access-token","refresh_token":"fresh-refresh-token"}
        """;

    private const string UserInfoResponse =
        """
        {"id":"00000000-1111-2222-3333-444444444444","displayName":"Reauth User","mail":"reauth-user@example.com","userPrincipalName":"reauth-user@example.com"}
        """;

    public StorageReauthenticatedBroadcastTests(PostgresFixture fixture) : base(fixture)
    {
    }


    [SkippableFact]
    public async Task Self_update_of_oauth_tokens_fires_exactly_one_StorageReauthenticated_broadcast()
    {
        await ArrangeAsync();

        var (storageId, stateId) = await _seedAsync();

        Api.OAuth2Transport = new StubOAuth2Transport()
            .RespondTo("/token", HttpStatusCode.OK, TokenResponse)
            .RespondTo("/me", HttpStatusCode.OK, UserInfoResponse);

        using var client = Api.CreateApiClient();

        var response = await client.GetAsync($"{Route}?code=the-authorization-code&state={stateId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ProcessOAuth2Result>();
        result.Should().NotBeNull();
        result!.Error.Should().BeNull("the exchange must have succeeded: {0}", result.ErrorDescription);
        result.AccessToken.Should().Be("fresh-access-token");
        result.RefreshToken.Should().Be("fresh-refresh-token");
        result.ClientId.Should().Be(BackerApiFactory.OneDriveClientId);

        /*
         * The tokens really were persisted - the broadcast below is therefore
         * about a genuine credential change, not about an empty write.
         */
        await using (var context = Fixture.CreateContext())
        {
            var storage = await context.Storages.SingleAsync(s => s.Id == storageId);
            storage.AccessToken.Should().Be("fresh-access-token");
            storage.RefreshToken.Should().Be("fresh-refresh-token");
            storage.ClientId.Should().Be(BackerApiFactory.OneDriveClientId);
            storage.ClientSecret.Should().Be(BackerApiFactory.OneDriveClientSecret);
            storage.UriSchema.Should().Be(UriSchema);

            var state = await context.OAuthStates.SingleAsync(s => s.Id == stateId);
            state.Used.Should().BeTrue();
        }

        /*
         * THE assertion. Exactly one broadcast, to everybody, naming the
         * storage's UriSchema - that is the contract the agents listen on
         * (HannibalServiceStorages.cs:169-172).
         */
        var broadcasts = Api.Hub.BroadcastsOf("StorageReauthenticated");

        broadcasts.Should().ContainSingle(
            "persisting OAuth2 tokens through the self-update path must notify the agents "
            + "exactly once - without the isSelfUpdate guard there is no broadcast at all. "
            + "Recorded: [{0}]",
            string.Join("; ", Api.Hub.Broadcasts));

        var broadcast = broadcasts[0];
        broadcast.Target.Should().Be("All");
        broadcast.Args.Should().ContainSingle();
        broadcast.Args[0].Should().Be(UriSchema);

        Api.Hub.Broadcasts.Should().HaveCount(
            1, "no other hub traffic may be produced by an OAuth2 callback");

        // The stub answered both legs; nothing went to the network.
        Api.OAuth2Transport!.Requests.Should().HaveCount(2);
    }


    /// <summary>
    /// Control case: an unrelated storage that was not re-authenticated must not
    /// be broadcast about, so the assertion above cannot pass by accident.
    /// </summary>
    [SkippableFact]
    public async Task Broadcast_carries_the_uri_schema_of_the_reauthenticated_storage_only()
    {
        await ArrangeAsync();

        await using (var seed = Fixture.CreateContext())
        {
            seed.Storages.Add(new Storage
            {
                UserId = "reauth-user",
                Technology = "dropbox",
                UriSchema = "AnUnrelatedDropbox",
                OAuth2Email = "someone-else@example.com",
                IsActive = true
            });
            await seed.SaveChangesAsync();
        }

        var (_, stateId) = await _seedAsync();

        Api.OAuth2Transport = new StubOAuth2Transport()
            .RespondTo("/token", HttpStatusCode.OK, TokenResponse)
            .RespondTo("/me", HttpStatusCode.OK, UserInfoResponse);

        using var client = Api.CreateApiClient();
        var response = await client.GetAsync($"{Route}?code=the-authorization-code&state={stateId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var broadcasts = Api.Hub.BroadcastsOf("StorageReauthenticated");
        broadcasts.Should().ContainSingle();
        broadcasts[0].Args[0].Should().Be(UriSchema);
        broadcasts[0].Args[0].Should().NotBe("AnUnrelatedDropbox");
    }


    /// <summary>
    /// Seeds the <see cref="Storage"/> that
    /// <c>ProcessOAuth2ResultAsync:190-193</c> looks up
    /// (<c>Technology == provider &amp;&amp; OAuth2Email == state.UserId</c>)
    /// plus the matching, unused, fresh <see cref="OAuthState"/>.
    /// </summary>
    private async Task<(int StorageId, Guid StateId)> _seedAsync()
    {
        await using var context = Fixture.CreateContext();

        var storage = new Storage
        {
            UserId = "reauth-user",
            Technology = "onedrive",
            UriSchema = UriSchema,
            Networks = "",
            OAuth2Email = OAuth2Email,
            ClientId = "stale-client-id",
            ClientSecret = "stale-client-secret",
            AccessToken = "stale-access-token",
            RefreshToken = "stale-refresh-token",
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
            IsActive = true
        };
        context.Storages.Add(storage);

        var state = new OAuthState
        {
            UserId = OAuth2Email,
            Provider = "onedrive",
            ReturnUrl = "https://backer.example.com/storages",
            CreatedAt = DateTime.UtcNow,
            Used = false
        };
        context.OAuthStates.Add(state);

        await context.SaveChangesAsync();

        return (storage.Id, state.Id);
    }
}
