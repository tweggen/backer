using WorkerRClone.Models;

namespace WorkerRClone.Services;

/// <summary>
/// Configuration for a single state including entry/exit actions and valid transitions
/// </summary>
public class StateConfig
{
    public RCloneServiceState.ServiceState State { get; init; }
    public Func<Task>? OnEnter { get; init; }
    public Func<Task>? OnExit { get; init; }
    public Dictionary<ServiceEvent, RCloneServiceState.ServiceState> Transitions { get; init; } = new();
}
