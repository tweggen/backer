namespace Higgins.Models;

public class Route
{
    public int Id { get; set; }
    public User User { get; set; }
    public string From { get; set; }
    public string To { get; set; }
}
