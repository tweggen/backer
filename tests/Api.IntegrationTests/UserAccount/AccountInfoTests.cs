using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.IntegrationTests.Fixtures;
using FluentAssertions;

namespace Api.IntegrationTests.UserAccount;

/// <summary>
/// REQ-006: Account Information Display
/// Tests for GET /api/hannibal/v1/users/{id} endpoint
/// </summary>
public class AccountInfoTests : IClassFixture<BackerWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AccountInfoTests(BackerWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    #region AC1 - Account page URL

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify page at /backer/account with [Authorize] attribute.")]
    [Trait("Category", "UI")]
    public void AccountPage_IsAvailableAtCorrectUrl() { }

    #endregion

    #region AC2 - Get user information

    [Fact]
    [Trait("Category", "API")]
    public async Task GetUser_ReturnsUserEmail()
    {
        const string email = "acctinfo_email@test.com";
        await _client.RegisterUserAsync(email, "Password1");

        var tokenResponse = await _client.GetTokenAsync(email, "Password1");
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();

        var response = await _client.GetUserAsync(-1, token!.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("email").GetString().Should().Be(email);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task GetUser_RequiresAuthorization()
    {
        // Request without JWT should return 401
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/hannibal/v1/users/-1");
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task GetUser_NonMinusOne_ReturnsUnauthorized()
    {
        // GET /api/hannibal/v1/users/5 should return 401 per Program.cs line 207-209
        const string email = "acctinfo_nonminus1@test.com";
        await _client.RegisterUserAsync(email, "Password1");

        var tokenResponse = await _client.GetTokenAsync(email, "Password1");
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();

        var response = await _client.GetUserAsync(5, token!.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region AC3 - Logout link

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify logout link in card header (Account.razor:42).")]
    [Trait("Category", "UI")]
    public void AccountPage_HasLogoutLink() { }

    #endregion

    #region AC4 - Export, Import, Delete features

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify config export/import sections and delete button exist on account page.")]
    [Trait("Category", "UI")]
    public void AccountPage_HasExportImportAndDeleteFeatures() { }

    #endregion
}
