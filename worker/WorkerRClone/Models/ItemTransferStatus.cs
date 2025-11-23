namespace WorkerRClone.Models;

public class ItemTransferStatus
{
    public long BytesTransferred { get; set; }
    public double ETA { get; set; }
    public string Name { get; set; }
    public float PercentDone { get; set; }
    public float Speed { get; set; }
    public float AverageSpeed { get; set; }
    public long TotalSize { get; set; }
}