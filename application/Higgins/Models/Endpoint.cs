namespace Higgins.Models;


/**
 * Describes an endpoint of a monitoring or transfer as used by
 * a specific user.
 */
public class Endpoint
{
    public int Id { get; set; }
    public User User { get; set; }
    public Storage Storage { get; set; }
    public string Path { get; set; }
    
    public string Comment { get; set; }
}