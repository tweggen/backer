namespace Hannibal.Models;


public class Job
{
    public Job()
    {
    }


    public Job(Job o)
    {
        Id = o.Id;
        Tag = o.Tag;
        Owner = o.Owner;
        State = o.State;
        StartFrom = o.StartFrom;
        EndBy = o.EndBy;
        SourceEndpoint = o.SourceEndpoint;
        DestinationEndpoint = o.DestinationEndpoint;
        Status = o.Status;
    }

    /**
     * The job id as defined by Hannibal.
     */
    public int Id { get; set; }
    
    
    public string UserId { get; set; }
    
    /**
     * A tag given to the job by the author, derived from a regular backup part
     */
    public string Tag { get; set; }

    /** 
     * What shall be executed?
     */
    public Rule.RuleOperation Operation { get; set; }
    
    
    public int FromRuleId { get; set; }
    
    /**
     * If this job was created from applying a rule, this is the rule.
     */
    public virtual Rule? FromRule { get; set; }
    
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
 
    /**
     * This job is supposed to start earliest at this point in time.
     */
    public DateTime StartFrom { get; set; }
    
    /**
     * This job is supposed to end by that particular date.
     */
    public DateTime EndBy { get; set; }
    
    
    /**
     * When was this job reported back the last time?
     */
    public DateTime LastReported { get; set; }
    
    public int SourceEndpointId { get; set; }
    public virtual Endpoint SourceEndpoint { get; set; }
    
    public int DestinationEndpointId { get; set; }
    public virtual Endpoint DestinationEndpoint { get; set; }
    
    /**
     * The most recent status of the job.
     */
    public int Status { get; set; }
}