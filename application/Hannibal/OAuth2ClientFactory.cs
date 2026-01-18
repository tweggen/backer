using Hannibal.Configuration;
using OAuth2.Client;

namespace Hannibal;

public class OAuth2ClientFactory
{
    private OAuthOptions _oauthOptions;


    public OAuth2ClientFactory(OAuthOptions oauthOptions)
    {
        _oauthOptions = oauthOptions;
    }
    
    
    public void OnUpdateOptions(OAuthOptions? oauthOptions)
    {
        if (null == oauthOptions) return;
        _oauthOptions = oauthOptions;
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
            new OAuth2.Infrastructure.RequestFactory(),
            new OAuth2.Configuration.ClientConfiguration
            {
                ClientId = provider.ClientId.Trim(),
                ClientSecret = (provider.ClientSecret ?? "").Trim(),
                RedirectUri = "http://localhost:53682/",
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
            new OAuth2.Infrastructure.RequestFactory(),
            new OAuth2.Configuration.ClientConfiguration
            {
                ClientId = provider.ClientId.Trim(),
                ClientSecret = provider.ClientSecret.Trim(),
                RedirectUri = "http://localhost:53682/",
                Scope = "files.metadata.write files.content.write files.content.read sharing.write account_info.read",
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
            default:
                throw new KeyNotFoundException("provider not found.");
        }

        return oauth2Client;
    }
}