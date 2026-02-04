namespace WorkerRClone.Models;

public class TransferStatsResult
{
    public List<ItemTransferStatus>? TransferringItems { get; set; }

    /// <summary>
    /// Aggregate statistics across all transfers
    /// </summary>
    public OverallTransferStats? OverallStats { get; set; }
}