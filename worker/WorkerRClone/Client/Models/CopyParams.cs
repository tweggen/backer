namespace WorkerRClone.Client.Models;

public class CopyParams
{
    public bool _async { get; set; }
    public string srcFs { get; set; }
    public string dstFs { get; set; }
    
    public bool createEmptySrcDirs { get; set; }

    public Config _config { get; set; } = new();

    public Filter _filter { get; set; } = new();
}