namespace Hannibal.Configuration;

public class HannibalServiceOptions
{
    public int MaxConcurrentJobs { get; set; }
    public string RclonePath { get; set; }
    public string RcloneEnvironment { get; set; }
}