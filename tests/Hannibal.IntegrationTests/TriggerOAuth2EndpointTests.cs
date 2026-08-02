using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate C: <c>POST /api/hannibal/v1/users/triggerOAuth2</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TriggerOAuth2EndpointTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";
    private const string Route = "/api/hannibal/v1/users/triggerOAuth2";

    public TriggerOAuth2EndpointTests(PostgresFixture fixture) : base(fixture)
    {
    }


    [SkippableFact]
    public async Task Without_a_bearer_token_the_endpoint_returns_401_and_creates_no_state()
    {
        await ArrangeAsync();

        using var client = Api.CreateApiClient();

        var response = await client.PostAsJsonAsync(Route, new OAuth2Params
        {
            Provider = "onedrive",
            UserLogin = "anonymous@example.com",
            AfterAuthUri = "https://backer.example.com/storages",
            DoDisconnect = false
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var context = Fixture.CreateContext();
        (await context.OAuthStates.CountAsync()).Should().Be(0);
    }


    [SkippableFact]
    public async Task Authenticated_request_returns_a_microsoft_authorize_url_and_one_state_row()
    {
        await ArrangeAsync();

        const string userLogin = "trigger-user@example.com";
        const string afterAuthUri = "https://backer.example.com/storages?reauth=1";

        using var client = await CreateAuthenticatedClientAsync(
            UniqueEmail("trigger-oauth2"), Password);

        var response = await client.PostAsJsonAsync(Route, new OAuth2Params
        {
            Provider = "onedrive",
            UserLogin = userLogin,
            AfterAuthUri = afterAuthUri,
            DoDisconnect = false
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<TriggerOAuth2Result>();
        result.Should().NotBeNull();

        /*
         * Exactly one state row, and it is the one referenced by the URL.
         */
        await using var context = Fixture.CreateContext();
        var state = (await context.OAuthStates.ToListAsync()).Should().ContainSingle().Subject;

        state.Provider.Should().Be("onedrive");
        state.ReturnUrl.Should().Be(afterAuthUri);
        state.UserId.Should().Be(userLogin);
        state.Used.Should().BeFalse();
        state.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        var redirectUrl = result!.RedirectUrl;
        redirectUrl.Should().StartWith(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize");
        redirectUrl.Should().Contain("response_type=code");
        redirectUrl.Should().Contain($"state={state.Id}");

        // The stub credential, never the developer's real user secret.
        redirectUrl.Should().Contain($"client_id={BackerApiFactory.OneDriveClientId}");

        /*
         * OAuth2:RedirectUri left unset, so the production default is used.
         * RestSharp 106 percent-encodes with lowercase hex digits
         * (http%3a%2f%2f...), hence the case insensitive comparison.
         */
        redirectUrl.Should().ContainEquivalentOf("redirect_uri=http%3A%2F%2Flocalhost%3A53682%2F");
    }


    /// <summary>
    /// Documents an existing behaviour rather than endorsing it: the endpoint
    /// maps <em>every</em> exception to 404, so an unknown provider and an
    /// internal failure are indistinguishable to the caller
    /// (<c>Api/Program.cs:267-270</c>).
    /// </summary>
    [SkippableFact]
    public async Task Unknown_provider_is_reported_as_404_by_the_endpoints_catch_all()
    {
        await ArrangeAsync();

        using var client = await CreateAuthenticatedClientAsync(
            UniqueEmail("trigger-unknown-provider"), Password);

        var response = await client.PostAsJsonAsync(Route, new OAuth2Params
        {
            Provider = "not-a-provider",
            UserLogin = "trigger-user@example.com",
            AfterAuthUri = "https://backer.example.com/storages",
            DoDisconnect = false
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var context = Fixture.CreateContext();
        (await context.OAuthStates.CountAsync()).Should().Be(
            0, "the provider lookup happens before the state row is written");
    }
}
