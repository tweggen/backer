namespace Hannibal.Configuration;

public class OAuthOptions
{
    public SortedDictionary<string, OAuthProviderOptions> Providers { get; set; } = new();

    /// <summary>
    /// OAuth2 redirect URI registered with the providers. When left unset the
    /// factory falls back to <see cref="OAuth2ClientFactory.DefaultRedirectUri"/>
    /// ("http://localhost:53682/"), which is what the code used to hardcode.
    /// </summary>
    public string? RedirectUri { get; set; }

    public OAuthOptions()
    {
    }


    public OAuthOptions(OAuthOptions o)
    {
        RedirectUri = o.RedirectUri;
        Providers = new SortedDictionary<string, OAuthProviderOptions>();
        foreach (var kvp in o.Providers)
        {
            Providers[kvp.Key] = new OAuthProviderOptions(kvp.Value);
        }
    }
}