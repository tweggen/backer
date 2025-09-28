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