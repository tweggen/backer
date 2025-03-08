using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Hannibal.Models;

/**
 * Describes generic credentials/config for an entity.
 */
public class Credentials
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public virtual User User { get; set; }

    [NotMapped]
    public Dictionary<string, string> Environment { get; set; } = new();
    
    public string EnvironmentJson
    {
        get => JsonSerializer.Serialize(Environment);
        set => Environment = JsonSerializer.Deserialize<Dictionary<string, string>>(value);
    }
}