namespace WorkerRClone.Configuration;

public class RCloneServiceOptions
{
    public string? BackerUsername { get; set; }
    
    public string? BackerPassword { get; set; }
    
    /**
     * Where can we find the rclone executable?
     */
    public string? RClonePath { get; set; }
    
    public string? RCloneOptions { get; set; }
    
    public string? UrlSignalR { get; set; }


    public RCloneServiceOptions(RCloneServiceOptions o)
    {
        BackerUsername = o.BackerUsername;
        BackerPassword = o.BackerPassword;
        RClonePath = o.RClonePath;
        RCloneOptions = o.RCloneOptions;
        UrlSignalR = o.UrlSignalR;
    }

    public RCloneServiceOptions()
    {
    }
}