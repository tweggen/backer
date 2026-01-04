using System.Text.Json.Serialization;

namespace WorkerRClone.Client.Models;

public class RemoteOptions
{
    [JsonPropertyName("obscure")]
    public bool Obscure { get; set; } = false;
    [JsonPropertyName("noObscure")]
    public bool NoObscure { get; set; } = true;
    [JsonPropertyName("noOutput")]
    public bool NoOutput { get; set; } = false;
    [JsonPropertyName("nonInteractive")]
    public bool NonInteractive { get; set; } = true; 
    [JsonPropertyName("continue")]
    public bool DoContinue { get; set; } = true;
    [JsonPropertyName("all")] 
    public bool All { get; set; } = false;
    [JsonPropertyName("state")]
    public bool State { get; set; } = false;
    [JsonPropertyName("result")]
    public bool Result { get; set; } = false;
}