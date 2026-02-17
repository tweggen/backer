using System.Net;
using System.Net.Http.Json;
using Api.IntegrationTests.Fixtures;
using FluentAssertions;

namespace Api.IntegrationTests.UserAccount;

/// <summary>
/// REQ-005: Account Deletion
/// Tests for DELETE /api/authb/v1/deleteUser endpoint
/// </summary>
public class AccountDeletionTests : IClassFixture<BackerWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AccountDeletionTests(BackerWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    #region AC1 - Delete button in danger zone

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify delete button exists in danger zone section of /backer/account page.")]
    [Trait("Category", "UI")]
    public void AccountPage_HasDeleteButtonInDangerZone() { }

    #endregion

    #region AC2 - Confirmation dialog

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify JS confirm dialog appears when delete button is clicked (Account.razor:314).")]
    [Trait("Category", "UI")]
    public void AccountPage_DeleteShowsConfirmation() { }

    #endregion

    #region AC3 - User deletion

    [Fact]
    [Trait("Category", "API")]
    public async Task DeleteUser_RemovesUserFromDatabase()
    {
        // Register a user
        await _client.RegisterUserAsync("delete_user@test.com", "Password1");

        // Get token to extract user ID
        var tokenResponse = await _client.GetTokenAsync("delete_user@test.com", "Password1");
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        var userId = HttpClientExtensions.ExtractUserIdFromJwt(token!.AccessToken);

        // Delete the user
        var deleteResponse = await _client.DeleteUserAsync(userId);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify the user can no longer login
        var loginResponse = await _client.GetTokenAsync("delete_user@test.com", "Password1");
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task DeleteUser_NonexistentUser_ReturnsNotFound()
    {
        var response = await _client.DeleteUserAsync("00000000-0000-0000-0000-000000000000");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task DeleteUser_NoAuthRequired_SecurityGap()
    {
        // This test documents that the deleteUser endpoint lacks RequireAuthorization().
        // An unauthenticated request should still succeed (security gap).
        await _client.RegisterUserAsync("delete_noauth@test.com", "Password1");

        var tokenResponse = await _client.GetTokenAsync("delete_noauth@test.com", "Password1");
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        var userId = HttpClientExtensions.ExtractUserIdFromJwt(token!.AccessToken);

        // Delete without any auth token - this should still work (no RequireAuthorization on endpoint)
        var deleteResponse = await _client.DeleteUserAsync(userId);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the deleteUser endpoint currently lacks RequireAuthorization() - this is a security gap");
    }

    #endregion

    #region AC4 - Redirect after deletion

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify navigation to /login after account deletion (Account.razor:328).")]
    [Trait("Category", "UI")]
    public void AccountPage_DeleteRedirectsToLogin() { }

    #endregion
}
