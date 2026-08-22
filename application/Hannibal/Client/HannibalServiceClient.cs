using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Client.Configuration;
using Hannibal.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Tools;
using Endpoint = Hannibal.Models.Endpoint;

namespace Hannibal.Client;

public partial class HannibalServiceClient : IHannibalServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ITokenProvider? _tokenProvider;
    
    public HannibalServiceClient(
        IOptions<HannibalServiceClientOptions> options,
        HttpClient httpClient
        //IHttpContextAccessor? httpContextAccessor = null,
        //ITokenProvider? tokenProvider = null
    )
    {
        _httpClient = httpClient;
        _httpContextAccessor = null; //httpContextAccessor;
        _tokenProvider = null; //tokenProvider;
    }

    
    /**
     * Like EnsureSuccessStatusCode, but keeps the server's message. The Api
     * answers validation failures with 400 and a JSON body {"error": "..."}
     * naming the offending field (storage/endpoint/rule validation from the
     * git-storage work); EnsureSuccessStatusCode discards that body, leaving
     * the UI with a bare "400 (Bad Request)". Used on the mutating calls
     * whose failures a user is expected to read and act on.
     */
    private static async Task _ensureSuccessWithServerMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = "";
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                detail = error.GetString() ?? "";
            }
        }
        catch
        {
            // Not JSON or unreadable - fall through to the generic message.
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            response.EnsureSuccessStatusCode();
        }

        throw new HttpRequestException(detail, null, response.StatusCode);
    }


    private async Task SetAuthorizationHeader()
    {
        string? token = null;
        
        // For Blazor Server
        if (string.IsNullOrEmpty(token) && _httpContextAccessor?.HttpContext != null)
        {
            var user =  _httpContextAccessor.HttpContext?.User;
            if (user != null && user.Identity.IsAuthenticated)
            {
                var claims = user.Claims.ToList();
                var authToken = claims.FirstOrDefault(c => c.Type == "access_token");
                if (authToken != null)
                {
                    token = authToken.Value;
                }
            }
        }
        if (string.IsNullOrEmpty(token) && _tokenProvider != null)
        {
            var accessToken = await _tokenProvider.GetToken();
            if (null != accessToken)
            {
                token = accessToken;
            }
        }

        if (!string.IsNullOrEmpty(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        
    }
    
    
    public IHannibalServiceClient SetAuthCookie(string authCookie)
    {
        return this;
    }
    

    public async Task<IdentityUser?> GetUserAsync(int id, CancellationToken cancellationToken)
    {
        await SetAuthorizationHeader();
        
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/users/{id}",
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (String.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            return JsonSerializer.Deserialize<IdentityUser>(
                content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            return null;
        }
    }


    public async Task<TriggerOAuth2Result> TriggerOAuth2Async(
        OAuth2Params oAuth2Params,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/hannibal/v1/users/triggerOAuth2",
            oAuth2Params, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (String.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            return JsonSerializer.Deserialize<TriggerOAuth2Result>(
                content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            return null;
        }
    }
    

    public async Task<ProcessOAuth2Result> ProcessOAuth2ResultAsync(
        HttpRequest httpRequest,
        string? code,
        string? state,
        string? error,
        string? errorDescription,
        CancellationToken cancellationToken)
    {
        var queryParams = new Dictionary<string, string?>
        {
            ["code"] = code, ["state"] = state, 
            ["error"] = error, ["error_description"] = errorDescription 
        };
        
        var url = QueryHelpers.AddQueryString("/api/hannibal/v1/users/processOAuth2Result", queryParams);
        
        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (String.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ProcessOAuth2Result>(
                content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            return null;
        }
    }


    public Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}