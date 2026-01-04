namespace Hannibal.Models;

public class ProcessOAuth2Result
{
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }

    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime ExpiresAt { get; set; }

    public string? AfterAuthUri { get; set; }
}