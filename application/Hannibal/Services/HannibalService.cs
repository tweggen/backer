using System.Collections.Generic;
using System.Security.Claims;
using System.Collections.Specialized;
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
using OAuth2.Client;
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
    private readonly IOAuthStateService _oauthStateService;

    private readonly IServiceProvider _serviceProvider;

    
    public HannibalService(
        HannibalContext context,
        ILogger<HannibalService> logger,
        IOptions<HannibalServiceOptions> options,
        IOptions<OAuthOptions> oauthOptions,
        IHubContext<HannibalHub> hannibalHub,
        UserManager<IdentityUser> userManager,
        IOAuthStateService oauthStateService,
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider serviceProvider)
    {
        _context = context;
        _logger = logger;
        _options = options.Value;
        _oauthOptions = oauthOptions.Value;
        _hannibalHub = hannibalHub;
        _userManager = userManager;
        _oauthStateService = oauthStateService;
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
    

    private OAuth2.Client.Impl.DropboxClient _createDropboxOAuthClient()
    {
        if (!_oauthOptions.Providers.TryGetValue("dropbox", out var provider))
        {
            throw new KeyNotFoundException("provider dropbox not found");
        }

        var oauth2Client = new OAuth2.Client.Impl.DropboxClient(
            new OAuth2.Infrastructure.RequestFactory(),
            new OAuth2.Configuration.ClientConfiguration
            {
                ClientId = provider.ClientId.Trim(),
                ClientSecret = provider.ClientSecret.Trim(),
                RedirectUri = "http://localhost:5288/api/hannibal/v1/oauth2/dropbox",
                Scope = "account_info.read files.metadata.write files.metadata.read files.content.write files.content.read"
            });
        return oauth2Client;
    }


    private OAuth2Client _createOAuth2Client(string provider)
    {
        OAuth2Client oauth2Client;
        switch (provider)
        {
            case "onedrive":
                oauth2Client = _createMicrosoftOAuthClient();
                break;
            case "dropbox":
                oauth2Client = _createDropboxOAuthClient();
                break;
            default:
                throw new KeyNotFoundException("provider not found.");
        }

        return oauth2Client;
    }
    

    public async Task<TriggerOAuth2Result> TriggerOAuth2Async(
        OAuth2Params authParams, CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        if (!_oauthOptions.Providers.TryGetValue(authParams.Provider, out var provider))
        {
            throw new KeyNotFoundException($"provider {authParams.Provider} not found");
        }
        
        var stateId = await _oauthStateService.CreateAsync( 
            userId: authParams.UserLogin, 
            provider: authParams.Provider, 
            returnUrl: authParams.AfterAuthUri, 
            cancellationToken );

        OAuth2.Client.OAuth2Client? oauth2Client = _createOAuth2Client(authParams.Provider);
        
        return new()
        {
            RedirectUrl = await oauth2Client.GetLoginLinkUriAsync(
                stateId.ToString(),
                cancellationToken)
        };
    }



    public async Task<ProcessOAuth2Result> ProcessOAuth2ResultAsync(
        HttpRequest httpRequest,
        string callbackProvider,
        CancellationToken cancellationToken)
    {
        OAuth2.Client.OAuth2Client? oauth2Client = _createOAuth2Client(callbackProvider);

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
            var state = httpRequest.Query["state"];

            if (string.IsNullOrWhiteSpace(code))
            {
                throw new UnauthorizedAccessException("No temporary code returned.");
            }
            if (string.IsNullOrWhiteSpace(state))
            {
                throw new UnauthorizedAccessException("No state returned.");
            }
            OAuthState? stateEntry = null;
            try
            {
                var userInfo = await oauth2Client.GetUserInfoAsync(
                    new NameValueCollection()
                    {
                        { "code", code },
                        { "state", state }
                    });

                var stateId = new Guid(oauth2Client.State); 
                stateEntry = await _oauthStateService.ValidateAsync(
                    new Guid(oauth2Client.State), 
                    callbackProvider, cancellationToken);

                if (stateEntry == null)
                {
                    throw new UnauthorizedAccessException("State not found");
                }
                
                if (!string.IsNullOrWhiteSpace(userInfo.Email) && userInfo.Email != stateEntry.UserId)
                {
                    throw new UnauthorizedAccessException("User id mismatch");
                }
                
                await _oauthStateService.MarkUsedAsync(stateId, cancellationToken);
                
                /*
                 * Now that this was successful, store AccessToken and RefreshToken
                 * with the Storage object in question.
                 */
                
                // TXWTODO: You could optimize this.
                Storage sto = await _context.Storages.FirstAsync(s =>
                        s.Technology == stateEntry.Provider && s.OAuth2Email == stateEntry.UserId,
                    cancellationToken);

                var accessToken = oauth2Client.AccessToken;
                if (null == accessToken) accessToken = "";
                var refreshToken = oauth2Client.RefreshToken;
                if (null == refreshToken) refreshToken = "";
                sto.AccessToken = accessToken;
                sto.RefreshToken = refreshToken;
                sto.ExpiresAt = oauth2Client.ExpiresAt;
                
                await UpdateStorageAsync(sto.Id, sto, cancellationToken);

                return new ProcessOAuth2Result()
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    ExpiresAt = oauth2Client.ExpiresAt,
                    AfterAuthUri = stateEntry.ReturnUrl
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
                    AfterAuthUri = stateEntry?.ReturnUrl
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

