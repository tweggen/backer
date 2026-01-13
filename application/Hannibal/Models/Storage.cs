namespace Hannibal.Models;

public class Storage
{
    public int Id { get; set; }
    public string UserId { get; set; }
    public string Technology { get; set; }
    public string UriSchema { get; set; }
    public string Networks { get; set; }

    public string OAuth2Email { get; set; }

    public string ClientId { get; set; }
    public string ClientSecret { get; set; }
    public string AccessToken { get; set; }
    public string RefreshToken { get; set; }
    public DateTime ExpiresAt { get; set; }

    private DateTime _createdAt;

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    private DateTime _updatedAt;

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => _updatedAt = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    public bool IsActive { get; set; }

    // -----------------------------
    // Parameterless constructor
    // -----------------------------
    public Storage()
    {
        UserId = "";
        Technology = Technologies.GetTechnologies().FirstOrDefault();
        UriSchema = "";
        Networks = "";
        OAuth2Email = "";
        ClientId = "";
        ClientSecret = "";
        AccessToken = "";
        RefreshToken = "";
        ExpiresAt = DateTime.UtcNow;
        IsActive = true;

        // Initialize timestamps
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    // -----------------------------
    // Copy constructor
    // -----------------------------
    public Storage(Storage other)
    {
        if (other == null)
            throw new ArgumentNullException(nameof(other));

        Id = other.Id;
        UserId = other.UserId;
        Technology = other.Technology;
        UriSchema = other.UriSchema;
        Networks = other.Networks;

        OAuth2Email = other.OAuth2Email;

        ClientId = other.ClientId;
        ClientSecret = other.ClientSecret;
        AccessToken = other.AccessToken;
        RefreshToken = other.RefreshToken;
        ExpiresAt = other.ExpiresAt;

        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;

        IsActive = other.IsActive;
    }

    // -----------------------------
    // Default factory
    // -----------------------------
    public static Storage Default() => new Storage();
}