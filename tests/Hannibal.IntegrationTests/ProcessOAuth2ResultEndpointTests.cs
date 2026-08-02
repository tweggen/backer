using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate C: <c>GET /api/hannibal/v1/users/processOAuth2Result</c> (anonymous).
///
/// These tests assert what the code does today. Where that differs from the
/// wording of the gate, the difference is called out in the test name and
/// comment rather than papered over - see
/// <see cref="Unknown_state_is_surfaced_as_500_by_the_endpoints_catch_all"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProcessOAuth2ResultEndpointTests : ApiIntegrationTestBase
{
    private const string Route = "/api/hannibal/v1/users/processOAuth2Result";

    public ProcessOAuth2ResultEndpointTests(PostgresFixture fixture) : base(fixture)
    {
    }


    /// <summary>
    /// <c>ProcessOAuth2ResultAsync</c> throws <see cref="UnauthorizedAccessException"/>
    /// ("State not found") for an unknown state; the endpoint's catch-all turns
    /// every exception into a bare 500.
    ///
    /// The gate asks for "an error rather than a 500". The behaviour observed is
    /// a 500 with an empty body. That is *not* a silent success, but it is also
    /// not distinguishable from an internal fault. Reported, not changed:
    /// fixing it means editing Api/Program.cs, which is outside this gate.
    /// </summary>
    [SkippableFact]
    public async Task Unknown_state_is_surfaced_as_500_by_the_endpoints_catch_all()
    {
        await ArrangeAsync();

        using var client = Api.CreateApiClient();

        var response = await client.GetAsync(
            $"{Route}?code=some-code&state={Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().NotBe(HttpStatusCode.OK, "a bogus state must never succeed");

        // Nothing was consumed or written.
        await using var context = Fixture.CreateContext();
        (await context.OAuthStates.CountAsync()).Should().Be(0);
    }


    [SkippableFact]
    public async Task Already_used_state_is_rejected_and_stays_used()
    {
        await ArrangeAsync();

        var state = await _seedStateAsync(used: true, createdAt: DateTime.UtcNow);

        using var client = Api.CreateApiClient();
        var response = await client.GetAsync($"{Route}?code=some-code&state={state.Id}");

        response.StatusCode.Should().Be(
            HttpStatusCode.InternalServerError,
            "OAuthStateService.ValidateAsync returns null for a used state, which "
            + "ProcessOAuth2ResultAsync turns into UnauthorizedAccessException");

        await using var context = Fixture.CreateContext();
        var stored = await context.OAuthStates.SingleAsync();
        stored.Used.Should().BeTrue();

        Api.Hub.Broadcasts.Should().BeEmpty("a rejected callback must not notify any agent");
    }


    [SkippableFact]
    public async Task State_older_than_ten_minutes_is_rejected()
    {
        await ArrangeAsync();

        var state = await _seedStateAsync(
            used: false, createdAt: DateTime.UtcNow.AddMinutes(-11));

        using var client = Api.CreateApiClient();
        var response = await client.GetAsync($"{Route}?code=some-code&state={state.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        await using var context = Fixture.CreateContext();
        var stored = await context.OAuthStates.SingleAsync();
        stored.Used.Should().BeFalse("an expired state is never consumed, only refused");

        Api.Hub.Broadcasts.Should().BeEmpty();
    }


    /// <summary>
    /// A state that is only nine minutes old is still inside the window, proving
    /// the previous test fails for the expiry and not for some other reason.
    /// </summary>
    [SkippableFact]
    public async Task State_younger_than_ten_minutes_passes_validation()
    {
        await ArrangeAsync();

        var state = await _seedStateAsync(
            used: false, createdAt: DateTime.UtcNow.AddMinutes(-9));

        /*
         * The stubbed profile deliberately reports a different address than the
         * state entry, so the flow fails on the e-mail comparison - a check that
         * is only reached once the state itself has been accepted. Unlike the
         * expired/used cases this surfaces as HTTP 200 with an Error message.
         */
        Api.OAuth2Transport = new TestSupport.StubOAuth2Transport()
            .RespondTo("/token", HttpStatusCode.OK,
                """{"access_token":"a","refresh_token":"r","token_type":"Bearer","expires_in":3600}""")
            .RespondTo("/me", HttpStatusCode.OK,
                """{"id":"1","displayName":"Nine Minutes","mail":"nine-minutes@example.com"}""");

        using var client = Api.CreateApiClient();
        var response = await client.GetAsync($"{Route}?code=some-code&state={state.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ProcessOAuth2Result>();
        result!.Error.Should().StartWith(
            "Unable to read user info:",
            "the state was accepted; what failed is the later e-mail comparison");
        result.Error.Should().Contain("User id mismatch");
    }


    [SkippableFact]
    public async Task Provider_reported_error_is_echoed_back_verbatim()
    {
        await ArrangeAsync();

        using var client = Api.CreateApiClient();

        var response = await client.GetAsync(
            $"{Route}?error=access_denied&error_description=The+user+denied+the+request");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ProcessOAuth2Result>();
        result.Should().NotBeNull();
        result!.Error.Should().Be("access_denied");
        result.ErrorDescription.Should().Be("The user denied the request");
        result.AccessToken.Should().BeNull();
        result.RefreshToken.Should().BeNull();

        Api.Hub.Broadcasts.Should().BeEmpty();
    }


    private async Task<OAuthState> _seedStateAsync(bool used, DateTime createdAt)
    {
        var state = new OAuthState
        {
            UserId = "process-user@example.com",
            Provider = "onedrive",
            ReturnUrl = "https://backer.example.com/storages",
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc),
            Used = used
        };

        await using var context = Fixture.CreateContext();
        context.OAuthStates.Add(state);
        await context.SaveChangesAsync();

        return state;
    }
}
