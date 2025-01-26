namespace Higgins.Models;

/**
 * Describes generic credentials/config for an entity.
 */
public class Credentials
{
    public User User { get; set; }
    public Dictionary<string, string> Environment { get; set; }
}