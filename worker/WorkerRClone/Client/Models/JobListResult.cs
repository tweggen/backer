using System.Text.Json.Serialization;

namespace WorkerRClone.Client.Models;

public class JobListResult
{
    [JsonPropertyName("executeId")]
    public string executeId { get; set; }
    
    [JsonPropertyName("finished_ids")]
    public List<int> finished_ids { get; set; }
    
    [JsonPropertyName("jobsids")]
    public List<int> jobsids { get; set; }
    
    [JsonPropertyName("running_ids")]
    public List<int> running_ids { get; set; }
}