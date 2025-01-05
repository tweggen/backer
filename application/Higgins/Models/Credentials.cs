namespace Higgins.Models;

public class Credentials
{
    public User User { get; set; }
    public Endpoint Endpoint { get; set; }
    public Dictionary<string, string> Environment { get; set; }
}