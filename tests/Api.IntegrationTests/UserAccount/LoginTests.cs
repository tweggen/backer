using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Api.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Api.IntegrationTests.UserAccount;

/// <summary>
/// REQ-002: User Login
/// Tests for user login via POST /api/auth/v1/login and POST /api/authb/v1/token
/// </summary>
public class LoginTests : IClassFixture<BackerWebApplicationFactory>
{
    private readonly BackerWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LoginTests(BackerWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task EnsureUserRegistered(string email, string password)
    {
        await _client.RegisterUserAsync(email, password);
    }

    #region AC1 - Login page UI

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify /login page renders email, password fields and submit button.")]
    [Trait("Category", "UI")]
    public void LoginPage_HasEmailPasswordAndSubmit() { }

    #endregion

    #region AC2 - Valid login returns success

    [Fact]
    [Trait("Category", "API")]
    public async Task Login_ValidCredentials_ReturnsSuccess()
    {
        await EnsureUserRegistered("login_valid@test.com", "Password1");

        var response = await _client.LoginUserAsync("login_valid@test.com", "Password1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region AC3 - Cookie session on login

    [Fact]
    [Trait("Category", "API")]
    public async Task Login_ValidCredentials_SetsCookieSession()
    {
        // Use a client that doesn't auto-handle cookies so we can see Set-Cookie headers
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false
        });

        await client.RegisterUserAsync("login_cookie@test.com", "Password1");

        var response = await client.LoginUserAsync("login_cookie@test.com", "Password1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().ContainKey("Set-Cookie");
    }

    #endregion

    #region AC4 - JWT token endpoint

    [Fact]
    [Trait("Category", "API")]
    public async Task Token_ValidCredentials_ReturnsJwt()
    {
        await EnsureUserRegistered("login_jwt@test.com", "Password1");

        var response = await _client.GetTokenAsync("login_jwt@test.com", "Password1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
        tokenResponse.Should().NotBeNull();
        tokenResponse!.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task Token_JwtContainsExpectedClaims()
    {
        await EnsureUserRegistered("login_claims@test.com", "Password1");
        var response = await _client.GetTokenAsync("login_claims@test.com", "Password1");
        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenResponse!.AccessToken);

        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub);
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Email);
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Jti);
        // ClaimTypes.Name is mapped to the "unique_name" or "name" claim in JWT
        jwt.Claims.Should().Contain(c =>
            c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"
            || c.Type == JwtRegisteredClaimNames.UniqueName);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task Token_JwtExpiresInOneHour()
    {
        await EnsureUserRegistered("login_expiry@test.com", "Password1");
        var response = await _client.GetTokenAsync("login_expiry@test.com", "Password1");
        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();

        tokenResponse!.ExpiresIn.Should().Be(3600);
    }

    #endregion

    #region AC5 - Invalid credentials

    [Fact]
    [Trait("Category", "API")]
    public async Task Login_InvalidPassword_ReturnsUnauthorized()
    {
        await EnsureUserRegistered("login_badpw@test.com", "Password1");

        var response = await _client.LoginUserAsync("login_badpw@test.com", "WrongPassword1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task Login_NonexistentUser_ReturnsUnauthorized()
    {
        var response = await _client.LoginUserAsync("nonexistent@test.com", "Password1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task Token_InvalidCredentials_ReturnsUnauthorized()
    {
        var response = await _client.GetTokenAsync("nonexistent_token@test.com", "Password1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region AC6 - Signup link on login page

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify link to /signup exists on login page.")]
    [Trait("Category", "UI")]
    public void LoginPage_HasSignupLink() { }

    #endregion
}
