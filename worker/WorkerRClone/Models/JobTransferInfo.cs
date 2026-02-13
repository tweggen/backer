namespace WorkerRClone.Models;

public class JobTransferInfo
{
    public int HannibalJobId { get; set; }
    public int RCloneJobId { get; set; }
    public string Tag { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? LastTransferActivity { get; set; }
    public List<ItemTransferStatus> Transfers { get; set; } = new();
    public OverallTransferStats? Stats { get; set; }
    public List<string> Errors { get; set; } = new();
    public int ErrorCount { get; set; }
}
