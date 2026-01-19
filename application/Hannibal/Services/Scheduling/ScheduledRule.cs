namespace Hannibal.Services.Scheduling;

/// <summary>
/// Represents a rule scheduled for future execution
/// </summary>
public class ScheduledRule
{
    public int RuleId { get; set; }
    public DateTime NextExecuteTime { get; set; }
    public ScheduleReason Reason { get; set; }
    
    /// <summary>
    /// Priority for execution when multiple rules are ready at same time
    /// Lower value = higher priority
    /// </summary>
    public int Priority { get; set; } = 0;
}

/// <summary>
/// Why a rule is scheduled for execution
/// </summary>
public enum ScheduleReason
{
    /// <summary>
    /// First time scheduling this rule
    /// </summary>
    InitialSchedule,
    
    /// <summary>
    /// MaxDestinationAge has expired since last successful job
    /// </summary>
    MaxAgeExpired,
    
    /// <summary>
    /// MinRetryTime has elapsed after a failed job
    /// </summary>
    RetryAfterFailure,
    
    /// <summary>
    /// A dependent job has completed
    /// </summary>
    DependencySatisfied,
    
    /// <summary>
    /// Manually triggered by user/admin
    /// </summary>
    ManualTrigger,
    
    /// <summary>
    /// Rule was modified, needs re-evaluation
    /// </summary>
    RuleModified,
    
    /// <summary>
    /// A job is currently in progress (Executing, Preparing, or Ready)
    /// </summary>
    JobInProgress
}
