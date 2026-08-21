namespace Hannibal.Models;


/**
 * Describe a rule that shall shall generate regular jobs.
 * It is the job of hannibal to plan the jobs.
 */
public class Rule
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Comment { get; set; } = "";
    
    public string UserId { get; set; }

    public int SourceEndpointId { get; set; }
    public virtual Endpoint SourceEndpoint { get; set; }
    
    public int DestinationEndpointId { get; set; }
    public virtual Endpoint DestinationEndpoint { get; set; }
    
    /**
     * The operation that shall be executed.
     */
    public enum RuleOperation 
    {
        Nop,
        Copy,
        Sync
    }

    public RuleOperation Operation { get; set; }
    
    /**
     * What is the maximal age of the most recent object in the
     * destination before a new operation must be triggered?
     */
    public TimeSpan MaxDestinationAge { get; set; }
    
    /**
     * When after the last failed try may we restart this
     * rule?
     */
    public TimeSpan MinRetryTime { get; set; }
    
    /**
     * How long after the latest modification in the source
     * must an operation be triggered?
     */
    public TimeSpan MaxTimeAfterSourceModification { get; set; }
    
    /**
     * What is the preferred time on any day to start the operation
     * if there is no urgent indication listed.
     */
    public TimeSpan DailyTriggerTime { get; set; }

    /**
     * Gate E (plan-git-repo-storage.md §5 "Adopt guard"): overrides the git
     * engine's refusal to push to a non-empty destination that carries no
     * matching refs/backer/mirror-of/<sha256> marker. Ignored by the rclone
     * engine. Defaults to false - adopting an existing destination is an
     * explicit, per-rule opt-in.
     */
    public bool AllowAdopt { get; set; }

    /**
     * Gate E (plan-git-repo-storage.md §5 "Shrink guard" and "Force-push
     * guard"): overrides both the shrink guard (refs removed on the
     * destination beyond Git:MaxRefShrinkPercent) and the force-push guard
     * (non-fast-forward ref updates beyond Git:MaxNonFfPercent). Ignored by
     * the rclone engine. Defaults to false.
     */
    public bool AllowUnsafeRefChange { get; set; }
}
