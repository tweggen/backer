namespace Hannibal.Configuration;

public class OAuthOptions
{
    public SortedDictionary<string, OAuthProviderOptions> Providers { get; set; } = new();
}