using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate C: <c>POST /api/authb/v1/token</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TokenEndpointTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";

    public TokenEndpointTests(PostgresFixture fixture) : base(fixture)
    {
    }


    [SkippableFact]
    public async Task Valid_credentials_return_a_jwt_that_validates_against_the_configured_key()
    {
        await ArrangeAsync();

        var email = UniqueEmail("token-valid");
        using var client = Api.CreateApiClient();

        var registration = await client.PostAsJsonAsync(
            "/api/auth/v1/register", new { email, password = Password });
        registration.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync(
            "/api/authb/v1/token", new LoginRequest { Email = email, Password = Password });

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the in-memory client speaks https, so UseHttpsRedirection must not have produced a 307");

        var payload = await response.Content.ReadFromJsonAsync<AccessTokenResponse>();
        payload.Should().NotBeNull();
        payload!.AccessToken.Should().NotBeNullOrWhiteSpace();
        payload.ExpiresIn.Should().Be(3600);

        /*
         * The host must be running on the harness' own JWT settings, not on the
         * ones in Api/appsettings.json.
         */
        var configuration = Api.Services.GetRequiredService<IConfiguration>();
        configuration["Jwt:Key"].Should().Be(BackerApiFactory.JwtKey);
        configuration["Jwt:Issuer"].Should().Be(BackerApiFactory.JwtIssuer);
        configuration["Jwt:Audience"].Should().Be(BackerApiFactory.JwtAudience);

        var handler = new JwtSecurityTokenHandler();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = BackerApiFactory.JwtIssuer,
            ValidAudience = BackerApiFactory.JwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(BackerApiFactory.JwtKey))
        };

        var act = () => handler.ValidateToken(payload.AccessToken, parameters, out _);
        act.Should().NotThrow("the token must validate against the configured signing key");

        handler.ValidateToken(payload.AccessToken, parameters, out var validated);

        var jwt = validated.Should().BeOfType<JwtSecurityToken>().Subject;
        jwt.Issuer.Should().Be(BackerApiFactory.JwtIssuer);
        jwt.Audiences.Should().Contain(BackerApiFactory.JwtAudience);

        using var scope = Api.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull();

        // Raw (unmapped) claims straight off the token.
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == user!.Id);
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Email && c.Value == email);
    }


    [SkippableFact]
    public async Task Wrong_password_returns_401()
    {
        await ArrangeAsync();

        var email = UniqueEmail("token-wrong-password");
        using var client = Api.CreateApiClient();

        var registration = await client.PostAsJsonAsync(
            "/api/auth/v1/register", new { email, password = Password });
        registration.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync(
            "/api/authb/v1/token",
            new LoginRequest { Email = email, Password = "N0tTheRightOne!" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }


    [SkippableFact]
    public async Task Unknown_user_returns_401()
    {
        await ArrangeAsync();

        using var client = Api.CreateApiClient();

        var response = await client.PostAsJsonAsync(
            "/api/authb/v1/token",
            new LoginRequest { Email = UniqueEmail("token-unknown"), Password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
