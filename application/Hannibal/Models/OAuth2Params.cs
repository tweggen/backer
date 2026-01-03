namespace Hannibal.Models;

public class OAuth2Params
{
    public string Provider { get; set; }
    public string UserLogin { get; set; }
    
    public string AfterAuthUri { get; set; }
}