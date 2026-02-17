using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Api.IntegrationTests.Fixtures;

public static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> RegisterUserAsync(
        this HttpClient client, string email, string password)
        => client.PostAsJsonAsync("/api/auth/v1/register", new { email, password });

    public static Task<HttpResponseMessage> LoginUserAsync(
        this HttpClient client, string email, string password)
        => client.PostAsJsonAsync("/api/auth/v1/login?useCookies=true", new { email, password });

    public static Task<HttpResponseMessage> GetTokenAsync(
        this HttpClient client, string email, string password)
        => client.PostAsJsonAsync("/api/authb/v1/token", new { email, password });

    public static Task<HttpResponseMessage> DeleteUserAsync(
        this HttpClient client, string userId)
        => client.DeleteAsync($"/api/authb/v1/deleteUser?userId={Uri.EscapeDataString(userId)}");

    public static Task<HttpResponseMessage> GetUserAsync(
        this HttpClient client, int id, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/hannibal/v1/users/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    public static string ExtractUserIdFromJwt(string accessToken)
    {
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(accessToken);
        return jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value;
    }
}

public class TokenResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("expiresIn")]
    public long ExpiresIn { get; set; }

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("tokenType")]
    public string TokenType { get; set; } = "";
}
