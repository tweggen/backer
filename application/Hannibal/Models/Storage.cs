namespace Hannibal.Models;


/**
 * Describes a data storage location for a specific user, like Timo's onedrive. 
 */
public class Storage
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public virtual User User { get; set; }
    public string Technology { get; set; }
    
    public string UriSchema { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsActive { get; set; }
    
}