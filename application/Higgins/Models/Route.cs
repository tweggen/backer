namespace Higgins.Models;

/**
 * Describes a specific route of data transfer.
 */
public class Route
{
    public int Id { get; set; }
    public User User { get; set; }
    public Endpoint FromEndpoint { get; set; }
    public Endpoint ToEndpoint { get; set; }
}
