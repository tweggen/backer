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
        Operation = o.Operation;
        FromRuleId = o.FromRuleId;
        Owner = o.Owner;
        State = o.State;
        StartFrom = o.StartFrom.ToUniversalTime();
        EndBy = o.EndBy.ToUniversalTime();
        LastReported = o.LastReported.ToUniversalTime();
        SourceEndpointId = o.SourceEndpointId;
        DestinationEndpointId = o.DestinationEndpointId;
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
        DoneSuccess,
        DoneWithErrors
    };
    
    public JobState State { get; set; }
 
    /**
     * This job is supposed to start earliest at this point in time.
     */
    private DateTime _startFrom;

    public DateTime StartFrom
    {
        get => _startFrom;
        set => _startFrom = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    private DateTime _endBy;

    /**
     * This job is supposed to end by that particular date.
     */
    public DateTime EndBy
    {
        get => _endBy;
        set => _endBy = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime(); 
        
    }
    

    private DateTime _lastReported;
    /**
     * When was this job reported back the last time?
     */
    public DateTime LastReported {
        get => _lastReported;
        set => _lastReported = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime(); 
    }
    
    public int SourceEndpointId { get; set; }
    public virtual Endpoint SourceEndpoint { get; set; }
    
    public int DestinationEndpointId { get; set; }
    public virtual Endpoint DestinationEndpoint { get; set; }
    
    /**
     * The most recent status of the job.
     */
    public int Status { get; set; }
}