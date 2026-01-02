using System.Collections.Generic;
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


    public async Task<TriggerOAuth2Result> TriggerOAuth2Async(
        OAuth2Params authParams, CancellationToken cancellationToken)
    {
        if (!_oauthOptions.Providers.TryGetValue(authParams.Provider, out var provider))
        {
            throw new KeyNotFoundException("provider not found");
        }

        var oauth2Client = new OAuth2.Client.Impl.WindowsLiveClient(
            new OAuth2.Infrastructure.RequestFactory(),
            new OAuth2.Configuration.ClientConfiguration
            {
                ClientId = provider.ClientId.Trim(),
                ClientSecret = provider.ClientSecret.Trim(),
                RedirectUri = "https://api.essentialvault.de/api/hannibal/v1/oauth2/microsoft",
                Scope = "profile email"
            });
        return new()
        {
            RedirectUrl = await oauth2Client.GetLoginLinkUriAsync(
                "SomeStateValueYouWantToUse", 
                cancellationToken)
        };
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

