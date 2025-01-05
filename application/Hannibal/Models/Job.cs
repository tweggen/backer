namespace Hannibal.Models;


public class Job
{
    public int Id { get; set; }
    
    public enum JobState
    {
        Preparing,
        Ready,
        Executing,
        DoneFailure,
        DoneSuccess
    };
    
    public JobState State { get; set; }
    
    public string FromUri { get; set; }
    public string ToUri { get; set; }
    
    public string ResultCode { get; set; }
}