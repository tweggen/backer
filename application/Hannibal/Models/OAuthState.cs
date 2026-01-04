namespace Hannibal.Models;

public class OAuthState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = default!;   // or email
    public string Provider { get; set; } = default!;
    public string ReturnUrl { get; set; } = default!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Used { get; set; } = false;
}