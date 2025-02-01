namespace Higgins.Models;


/**
 * Describes an endpoint of a monitoring or transfer as used by
 * a specific user.
 */
public class Endpoint
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int UserId { get; set; }
    public virtual User User { get; set; }
    public int StorageId { get; set; }
    public virtual Storage Storage { get; set; }
    public string Path { get; set; }
    
    public string Comment { get; set; }
}