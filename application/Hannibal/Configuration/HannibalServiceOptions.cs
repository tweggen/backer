namespace Hannibal.Configuration;

public class HannibalServiceOptions
{
    public int MaxConcurrentJobs { get; set; }
    
    /**
     * A job has to report back to the server every MaxTTL seconds, or
     * it is considered to be dead.
     */
    public int MaxTTL { get; set; }
    public string RclonePath { get; set; }
    public string RcloneEnvironment { get; set; }
}