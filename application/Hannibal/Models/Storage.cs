namespace Hannibal.Models;


/**
 * Describes a data storage location for a specific user, like Timo's onedrive. 
 */
public class Storage
{
    public int Id { get; set; }
    public string UserId { get; set; }
    public string Technology { get; set; }
    public string UriSchema { get; set; }
    public string Networks { get; set; }

    public string OAuth2Email { get; set; }
    public string AccessToken { get; set; }
    public string RefreshToken { get; set; }
    
    /*
     * A storage can be located in a particular network.
     * A client may or may not be connected to that network.
     * If the network is part of the client's network [list],
     * the client can access the storage.
     */
    // public string Network { get; set; }
    
    private DateTime _createdAt;

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    private DateTime _updatedAt;
    
    public DateTime UpdatedAt { 
        get => _updatedAt;
        set => _updatedAt = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
    
    public bool IsActive { get; set; }
    
}