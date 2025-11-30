using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Client.Configuration;
using Hannibal.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
    
    
    public Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}