using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity.Data;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;


namespace Hannibal.Client;

public class IdentityApiService : IIdentityApiService
{
    private readonly HttpClient _httpClient;

    public string ApiPrefix = "/api/auth/v1/";

    public IdentityApiService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    private async Task<T> SendRequestAsync<T>(HttpMethod method, string endpoint, object? data,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, endpoint);

        if (data != null)
        {
            var jsonData = JsonSerializer.Serialize(data);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(jsonResponse,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    
    public Task<Results<Ok, ValidationProblem>> RegisterUserAsync(RegisterRequest request,
        CancellationToken cancellationToken) =>
        SendRequestAsync<Results<Ok, ValidationProblem>>(HttpMethod.Post, $"{ApiPrefix}register", request,
            cancellationToken);

    public async Task<Results<Ok<AccessTokenResponse>, EmptyHttpResult, ProblemHttpResult>> LoginUserAsync(
        LoginRequest loginRequest, CancellationToken cancellationToken)
    {
        string endpoint = $"{ApiPrefix}login?useCookies=true&useSessionCookies=true";
        
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        if (loginRequest != null)
        {
            var jsonData = JsonSerializer.Serialize(loginRequest);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        
        if (response.IsSuccessStatusCode)
        {
            var contentString = await response.Content.ReadAsStringAsync(cancellationToken);
            IEnumerable<string> cookies = response.Headers.SingleOrDefault(header => header.Key == "Set-Cookie").Value;
            string? authToken= default;

            foreach (var cookie in cookies)
            {
                if (cookie.StartsWith(".AspNetCore.Identity.Application"))
                {
                    authToken = cookie.Substring(".AspNetCore.Identity.Application".Length);
                }
            }

            if (authToken != null)
            {
                return TypedResults.Ok(new AccessTokenResponse() { AccessToken = authToken, ExpiresIn = 3600, RefreshToken = ""});
            }

            return TypedResults.Empty;
        }
        else
        {
            // Try to parse error details
            var problemDetails = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken: cancellationToken)
                                 ?? new ProblemDetails { Title = "Login failed", Status = (int)response.StatusCode };
            
            return TypedResults.Problem(problemDetails);
        }
        
    }

    public Task<Results<Ok<AccessTokenResponse>, UnauthorizedHttpResult, SignInHttpResult, ChallengeHttpResult>>
        RefreshAsync(RefreshRequest refreshRequest, CancellationToken cancellationToken) =>
        SendRequestAsync<
            Results<Ok<AccessTokenResponse>, UnauthorizedHttpResult, SignInHttpResult, ChallengeHttpResult>>(
            HttpMethod.Post, $"{ApiPrefix}refresh", refreshRequest, cancellationToken);

    public Task<Results<ContentHttpResult, UnauthorizedHttpResult>> ConfirmEmail(string userId, string code,
        string? changedEmail, CancellationToken cancellationToken) =>
        SendRequestAsync<Results<ContentHttpResult, UnauthorizedHttpResult>>(HttpMethod.Get,
            $"{ApiPrefix}confirm-email?userId={userId}&code={code}&changedEmail={changedEmail}", null,
            cancellationToken);

    public Task<Ok> ResendConfirmationEmail(ResendConfirmationEmailRequest request,
        CancellationToken cancellationToken) =>
        SendRequestAsync<Ok>(HttpMethod.Post, $"{ApiPrefix}resend-confirmation", request, cancellationToken);

    public Task<Results<Ok, ValidationProblem>> ForgotPasswordAsync(ForgotPasswordRequest request,
        CancellationToken cancellationToken) =>
        SendRequestAsync<Results<Ok, ValidationProblem>>(HttpMethod.Post, $"{ApiPrefix}forgot-password", request,
            cancellationToken);

    public Task<Results<Ok, ValidationProblem>> ResetPasswordAsync(ResetPasswordRequest request,
        CancellationToken cancellationToken) =>
        SendRequestAsync<Results<Ok, ValidationProblem>>(HttpMethod.Post, $"{ApiPrefix}reset-password", request,
            cancellationToken);

    public Task<Results<Ok<TwoFactorResponse>, ValidationProblem, NotFound>> TwoFactorAsync(
        ClaimsPrincipal claimsPrincipal, TwoFactorRequest request, CancellationToken cancellationToken) =>
        SendRequestAsync<Results<Ok<TwoFactorResponse>, ValidationProblem, NotFound>>(HttpMethod.Post,
            $"{ApiPrefix}two-factor", request, cancellationToken);

    public Task<Results<Ok<InfoResponse>, ValidationProblem, NotFound>> GetInfo(ClaimsPrincipal claimsPrincipal,
        CancellationToken cancellationToken) =>
        SendRequestAsync<Results<Ok<InfoResponse>, ValidationProblem, NotFound>>(HttpMethod.Get, $"{ApiPrefix}info",
            null, cancellationToken);

    public Task<Results<Ok<InfoResponse>, ValidationProblem, NotFound>> SetInfo(ClaimsPrincipal claimsPrincipal,
        InfoRequest request, CancellationToken cancellationToken) =>
        SendRequestAsync<Results<Ok<InfoResponse>, ValidationProblem, NotFound>>(HttpMethod.Post,
            $"{ApiPrefix}set-info", request, cancellationToken);

    public Task<bool> DeleteUserAsync(string userId) =>
        SendRequestAsync<bool>(HttpMethod.Delete, $"{ApiPrefix}delete-user?userId={userId}", null,
            CancellationToken.None);

}
