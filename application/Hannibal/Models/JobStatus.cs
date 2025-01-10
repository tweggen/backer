namespace Hannibal.Models;

public class JobStatus
{
    /**
     * The id of the job as defined by hannibal
     */
    public int JobId { get; set; }

    /**
     * The textual id of the owner who promised to execute the job.
     */
    public string Owner { get; set; }
    
    /**
     * The status that has been reported.
     */
    public int Status { get; set; }
}