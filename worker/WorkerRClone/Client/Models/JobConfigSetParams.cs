using System.Text.Json.Serialization;

namespace WorkerRClone.Client.Models;

public class JobConfigSetParams
{     
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("key")]
    public string Key { get; set; } = default!;

    [JsonPropertyName("value")]
    public string Value { get; set; } = default!;
}