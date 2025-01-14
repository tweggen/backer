namespace WorkerRClone.Configuration;

public class RCloneServiceOptions
{
    /**
     * Where can we find the rclone executable?
     */
    public string RClonePath { get; set; }
    
    public string RCloneOptions { get; set; }
}