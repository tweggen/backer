namespace WorkerRClone.Client.Models;

public class SyncParams
{
    public bool _async { get; set; }
    public string srcFs { get; set; }
    public string dstFs { get; set; }
}