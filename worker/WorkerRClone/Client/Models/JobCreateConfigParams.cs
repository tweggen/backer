
using System.Text.Json.Serialization;

namespace WorkerRClone.Client.Models;

public class JobCreateConfigParams
{
    [JsonPropertyName("name")]
    public string Name { get; set; }
    
    [JsonPropertyName("parameters")]
    public SortedDictionary<string, string> Parameters { get; set; } = new();
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    
    [JsonPropertyName("opt")]
    public RemoteOptions Opt { get; set; } = new();
}