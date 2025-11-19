using System.Text.Json.Serialization;

namespace WorkerRClone.Client.Models;

public class JobStatsParams
{
    [JsonPropertyName("group")]
    public string? group { get; set; }
    
    [JsonPropertyName("short")]
    public bool? isShort { get; set; }
}