namespace Hannibal.Configuration;

public class OAuthProviderOptions
{
    public string ClientId { get; set; }
    public string ClientSecret { get; set; }

    
    public OAuthProviderOptions()
    {
    }
    
    
    public OAuthProviderOptions(OAuthProviderOptions o)
    {
        ClientId = o.ClientId;
        ClientSecret = o.ClientSecret;
    }
}