using Hannibal.Configuration;
using OAuth2.Client;
using OAuth2.Infrastructure;

namespace Hannibal;

public class OAuth2ClientFactory : IOAuth2ClientFactory
{
    /// <summary>
    /// Redirect URI used when <c>OAuth2:RedirectUri</c> is not configured.
    /// This is the loopback address rclone/the desktop flow listens on and is
    /// the value that used to be hardcoded in every client creation below.
    /// </summary>
    public const string DefaultRedirectUri = "http://localhost:53682/";

    private OAuthOptions _oauthOptions;

    /// <summary>
    /// Supplies the RestSharp transport used by the created clients. Defaults to
    /// a fresh <see cref="RequestFactory"/> per client (the previous behaviour);
    /// tests substitute this to keep the exchange entirely offline.
    /// </summary>
    private readonly Func<IRequestFactory> _requestFactoryProvider;


    public OAuth2ClientFactory(OAuthOptions oauthOptions)
        : this(oauthOptions, null)
    {
    }


    public OAuth2ClientFactory(
        OAuthOptions oauthOptions,
        Func<IRequestFactory>? requestFactoryProvider)
    {
        _oauthOptions = oauthOptions;
        _requestFactoryProvider = requestFactoryProvider ?? (() => new RequestFactory());
    }


    public void OnUpdateOptions(OAuthOptions? oauthOptions)
    {
        if (null == oauthOptions) return;
        _oauthOptions = oauthOptions;
    }


    /// <summary>
    /// The configured redirect URI, falling back to <see cref="DefaultRedirectUri"/>
    /// when <c>OAuth2:RedirectUri</c> is unset or blank.
    /// </summary>
    private string _redirectUri()
    {
        var configured = _oauthOptions.RedirectUri;
        return string.IsNullOrWhiteSpace(configured) ? DefaultRedirectUri : configured.Trim();
    }


    private OAuth2.Client.Impl.MicrosoftGraphClient _createMicrosoftOAuthClient(
        Guid stateId)
    {
        if (!_oauthOptions.Providers.TryGetValue("onedrive", out var provider))
        {
            throw new KeyNotFoundException("provider onedrive not found");
        }

        var oauth2Client = new OAuth2.Client.Impl.MicrosoftGraphClient(
            stateId,
            _requestFactoryProvider(),
            new OAuth2.Configuration.ClientConfiguration
            {
                ClientId = provider.ClientId.Trim(),
                ClientSecret = (provider.ClientSecret ?? "").Trim(),
                RedirectUri = _redirectUri(),
                Scope = "offline_access Files.ReadWrite User.Read"
            });
        return oauth2Client;
    }


    private OAuth2.Client.Impl.DropboxClient _createDropboxOAuthClient(
        Guid stateId)
    {
        if (!_oauthOptions.Providers.TryGetValue("dropbox", out var provider))
        {
            throw new KeyNotFoundException("provider dropbox not found");
        }

        var oauth2Client = new OAuth2.Client.Impl.DropboxClient(
            stateId,
            _requestFactoryProvider(),
            new OAuth2.Configuration.ClientConfiguration
            {
                ClientId = provider.ClientId.Trim(),
                ClientSecret = provider.ClientSecret.Trim(),
                RedirectUri = _redirectUri(),
                Scope = "files.metadata.write files.content.write files.content.read sharing.write account_info.read",
                IsOfflineToken = true
            });
        return oauth2Client;
    }


    private OAuth2.Client.Impl.GoogleClient _createGoogleOAuthClient(
        Guid stateId)
    {
        // Support both "google" and "googledrive" as provider keys
        if (!_oauthOptions.Providers.TryGetValue("googledrive", out var provider) &&
            !_oauthOptions.Providers.TryGetValue("google", out provider))
        {
            throw new KeyNotFoundException("provider googledrive/google not found");
        }

        var oauth2Client = new OAuth2.Client.Impl.GoogleClient(
            stateId,
            _requestFactoryProvider(),
            new OAuth2.Configuration.ClientConfiguration
            {
                ClientId = provider.ClientId.Trim(),
                ClientSecret = provider.ClientSecret.Trim(),
                RedirectUri = _redirectUri(),
                // Google Drive scopes - adjust based on access level needed
                // https://www.googleapis.com/auth/drive - Full access
                // https://www.googleapis.com/auth/drive.file - Per-file access
                Scope = "https://www.googleapis.com/auth/drive https://www.googleapis.com/auth/userinfo.email",
                IsOfflineToken = true
            });
        return oauth2Client;
    }


    public OAuth2Client CreateOAuth2Client(
        Guid stateId, string provider)
    {
        OAuth2Client oauth2Client;
        switch (provider)
        {
            case "onedrive":
                oauth2Client = _createMicrosoftOAuthClient(stateId);
                break;
            case "dropbox":
                oauth2Client = _createDropboxOAuthClient(stateId);
                break;
            case "google":
            case "googledrive":
                oauth2Client = _createGoogleOAuthClient(stateId);
                break;
            default:
                throw new KeyNotFoundException($"OAuth2 provider '{provider}' not found.");
        }

        return oauth2Client;
    }
}
