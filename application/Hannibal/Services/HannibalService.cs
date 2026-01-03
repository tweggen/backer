using System.Collections.Generic;
using System.Collections.Specialized;
using System.Security.Claims;
using System.Security.Cryptography;
using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Endpoint = Hannibal.Models.Endpoint;

namespace Hannibal.Services;


public partial class HannibalService : IHannibalService
{
    private readonly HannibalContext _context;
    private readonly ILogger<HannibalService> _logger;
    private readonly HannibalServiceOptions _options;
    private readonly OAuthOptions _oauthOptions;
    private readonly IHubContext<HannibalHub> _hannibalHub;
    private readonly IHttpContextAccessor _httpContextAccessor;
    
    private IdentityUser? _currentUser = null;
    
    private readonly UserManager<IdentityUser> _userManager;
    
    private readonly IServiceProvider _serviceProvider;

    public HannibalService(
        HannibalContext context,
        ILogger<HannibalService> logger,
        IOptions<HannibalServiceOptions> options,
        IOptions<OAuthOptions> oauthOptions,
        IHubContext<HannibalHub> hannibalHub,
        UserManager<IdentityUser> userManager,
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider serviceProvider)
    {
        _context = context;
        _logger = logger;
        _options = options.Value;
        _oauthOptions = oauthOptions.Value;
        _hannibalHub = hannibalHub;
        _userManager = userManager;
        _httpContextAccessor = httpContextAccessor;
        _serviceProvider = serviceProvider;
    }


    public async Task<IdentityUser?> GetUserAsync(int id, CancellationToken cancellationToken)
    {
        if (-1 != id)
        {
            throw new UnauthorizedAccessException("Access to different users notz permitted.");
        }
        var userClaims = _httpContextAccessor.HttpContext?.User;
        if (null != userClaims)
        {
            _currentUser = await _userManager.GetUserAsync(userClaims);
        }
        else
        {
            _currentUser = null;
        }

        return _currentUser;
    }


    private OAuth2.Client.Impl.MicrosoftGraphClient _createMicrosoftOAuthClient()
    {
        if (!_oauthOptions.Providers.TryGetValue("onedrive", out var provider))
        {
            throw new KeyNotFoundException("provider onedrive not found");
        }

        var oauth2Client = new OAuth2.Client.Impl.MicrosoftGraphClient(
            new OAuth2.Infrastructure.RequestFactory(),
            new OAuth2.Configuration.ClientConfiguration
            {
                ClientId = provider.ClientId.Trim(),
                ClientSecret = provider.ClientSecret.Trim(),
                RedirectUri = "http://localhost:5288/api/hannibal/v1/oauth2/microsoft",
                Scope = "User.Read"
            });
        return oauth2Client;
    }
    

    public async Task<TriggerOAuth2Result> TriggerOAuth2Async(
        OAuth2Params authParams, CancellationToken cancellationToken)
    {
        if (!_oauthOptions.Providers.TryGetValue(authParams.Provider, out var provider))
        {
            throw new KeyNotFoundException($"provider {authParams.Provider} not found");
        }

        switch (authParams.Provider)
        {
            case "onedrive":
            {
                var oauth2Client = _createMicrosoftOAuthClient();
                return new()
                {
                    RedirectUrl = await oauth2Client.GetLoginLinkUriAsync(
                        /* state */ authParams.AfterAuthUri,
                        cancellationToken)
                };
            }
            default:
                throw new KeyNotFoundException("provider not found.");
        }
    }


    public async Task<ProcessOAuth2Result> ProcessOAuth2ResultAsync(
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        var oauth2Client = _createMicrosoftOAuthClient();
        if (httpRequest.Query.ContainsKey("error"))
        {
            string? errorString = httpRequest.Query["error"];
            /*
             * This was not successful. Display the error.
             */
            return new ProcessOAuth2Result()
            {
                Error = httpRequest.Query["error"],
                ErrorDescription = httpRequest.Query["error_description"]
            };
        }
        else
        {
            /*
             * This was successful. Read the user info and the tokens.
             */
            var code = httpRequest.Query["code"];
            try
            {
                var userInfo = await oauth2Client.GetUserInfoAsync(
                    new NameValueCollection() { { "code", code } });
                string afterAuthUri = oauth2Client.State;
                return new ProcessOAuth2Result()
                {
                    AccessToken = oauth2Client.AccessToken,
                    RefreshToken = oauth2Client.RefreshToken,
                    ExpiresAt = oauth2Client.ExpiresAt,
                    AfterAuthUri = afterAuthUri
                };
                // TXWTODO: This still needs to put the access token for user into db
            }
            catch(Exception ex)
            {
                string afterAuthUri = oauth2Client.State;
                return new ProcessOAuth2Result()
                {
                    Error = "Unable to read user info",
                    ErrorDescription = $"Exception: {ex}",
                    AfterAuthUri = afterAuthUri
                };
            }
                
        }
    }



    private async Task _obtainUser()
    {
        var userClaims = _httpContextAccessor.HttpContext?.User;
        if (null != userClaims)
        {
            _currentUser = await _userManager.GetUserAsync(userClaims);
        }
        else
        {
            throw new UnauthorizedAccessException("User not found");
        }
    }
    
    
    public async Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationTokens)
    {
        return new ShutdownResult() { ErrorCode = 0 };
    }
}

