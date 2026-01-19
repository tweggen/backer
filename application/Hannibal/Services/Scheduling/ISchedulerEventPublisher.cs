namespace Hannibal.Services.Scheduling;

/// <summary>
/// Interface for publishing scheduler events.
/// Allows HannibalService to notify the scheduler without direct dependency.
/// </summary>
public interface ISchedulerEventPublisher
{
    /// <summary>
    /// Publish an event to the scheduler
    /// </summary>
    Task PublishEventAsync(SchedulerEvent schedulerEvent);
    
    /// <summary>
    /// Check if the publisher is available (scheduler might not be registered)
    /// </summary>
    bool IsAvailable { get; }
}

/// <summary>
/// Null implementation for when no scheduler is registered.
/// Used in environments where RuleScheduler is not enabled.
/// </summary>
public class NullSchedulerEventPublisher : ISchedulerEventPublisher
{
    public Task PublishEventAsync(SchedulerEvent schedulerEvent) => Task.CompletedTask;
    public bool IsAvailable => false;
}
