namespace Hannibal.Configuration;

public class OAuthOptions
{
    public SortedDictionary<string, OAuthProviderOptions> Providers { get; set; } = new();

    public OAuthOptions()
    {
    }
    
    
    public OAuthOptions(OAuthOptions o)
    {
        Providers = new SortedDictionary<string, OAuthProviderOptions>();
        foreach (var kvp in o.Providers)
        {
            Providers[kvp.Key] = new OAuthProviderOptions(kvp.Value);
        }
    }
}