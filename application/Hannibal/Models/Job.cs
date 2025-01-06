namespace Hannibal.Models;


public class Job
{
    /**
     * THe job id as defined by Hannibal.
     */
    public int Id { get; set; }
    
    /**
     * The textural owner of the job as reported. 
     */
    public string Owner { get; set; }
    
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
    
    /**
     * The most recent status of the job.
     */
    public int Status { get; set; }
}