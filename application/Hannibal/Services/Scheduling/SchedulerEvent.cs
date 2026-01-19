using Hannibal.Models;

namespace Hannibal.Services.Scheduling;

/// <summary>
/// Base class for scheduler events
/// </summary>
public abstract class SchedulerEvent
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Event fired when a job completes (success or failure)
/// </summary>
public class JobCompletedEvent : SchedulerEvent
{
    public int JobId { get; set; }
    public int RuleId { get; set; }
    public Job.JobState FinalState { get; set; }
}

/// <summary>
/// Event fired when jobs are deleted (e.g., via UI "Clear Jobs")
/// </summary>
public class JobsDeletedEvent : SchedulerEvent
{
    /// <summary>
    /// Rule IDs that had jobs deleted. Empty means all rules affected.
    /// </summary>
    public List<int> AffectedRuleIds { get; set; } = new();
}

/// <summary>
/// Event fired when a rule is created, updated, or deleted
/// </summary>
public class RuleChangedEvent : SchedulerEvent
{
    public int RuleId { get; set; }
    public RuleChangeType ChangeType { get; set; }
}

public enum RuleChangeType
{
    Created,
    Updated,
    Deleted,
    Enabled,
    Disabled
}

/// <summary>
/// Event fired when an endpoint is updated
/// </summary>
public class EndpointUpdatedEvent : SchedulerEvent
{
    public int EndpointId { get; set; }
}

/// <summary>
/// Event fired to manually trigger rule execution
/// </summary>
public class ManualTriggerEvent : SchedulerEvent
{
    public int RuleId { get; set; }
}
