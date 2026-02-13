namespace WorkerRClone.Models;

public class JobTransferStatsResult
{
    public List<JobTransferInfo> Jobs { get; set; } = new();
    public OverallTransferStats? OverallStats { get; set; }
}
