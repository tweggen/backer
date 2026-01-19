using Microsoft.Extensions.Logging;
using WorkerRClone.Models;

namespace WorkerRClone.Services;

/// <summary>
/// Table-driven state machine for managing RClone service states
/// </summary>
public class RCloneStateMachine
{
    private readonly Dictionary<RCloneServiceState.ServiceState, StateConfig> _stateConfigs;
    private readonly RCloneService _service;
    private RCloneServiceState.ServiceState _currentState;
    private readonly Queue<ServiceEvent> _pendingEvents = new();
    private readonly object _lock = new();
    
    public RCloneStateMachine(RCloneService service)
    {
        _service = service;
        _currentState = RCloneServiceState.ServiceState.Starting;
        
        // Define the state machine declaratively
        _stateConfigs = new()
        {
            [RCloneServiceState.ServiceState.Starting] = new()
            {
                State = RCloneServiceState.ServiceState.Starting,
                OnEnter = async () => _service._logger.LogInformation("RCloneService: Starting."),
                Transitions = new()
                {
                    [ServiceEvent.ConfigReceived] = RCloneServiceState.ServiceState.CheckOnline,
                    [ServiceEvent.ConfigInvalid] = RCloneServiceState.ServiceState.WaitConfig
                }
            },
            
            [RCloneServiceState.ServiceState.WaitConfig] = new()
            {
                State = RCloneServiceState.ServiceState.WaitConfig,
                OnEnter = async () => _service._logger.LogInformation("RCloneService: Waiting for configuration."),
                Transitions = new()
                {
                    [ServiceEvent.ConfigReceived] = RCloneServiceState.ServiceState.CheckOnline
                }
            },
            
            [RCloneServiceState.ServiceState.CheckOnline] = new()
            {
                State = RCloneServiceState.ServiceState.CheckOnline,
                OnEnter = async () => await _service._checkOnlineImpl(),
                Transitions = new()
                {
                    [ServiceEvent.OnlineCheckPassed] = RCloneServiceState.ServiceState.BackendsLoggingIn,
                    [ServiceEvent.OnlineCheckFailed] = RCloneServiceState.ServiceState.WaitConfig
                }
            },
            
            [RCloneServiceState.ServiceState.BackendsLoggingIn] = new()
            {
                State = RCloneServiceState.ServiceState.BackendsLoggingIn,
                OnEnter = async () => await _service._backendsLoginImpl(),
                Transitions = new()
                {
                    [ServiceEvent.BackendsLoggedIn] = RCloneServiceState.ServiceState.CheckRCloneProcess,
                    [ServiceEvent.StorageReauthenticationRequired] = RCloneServiceState.ServiceState.RestartingForReauth
                }
            },
            
            [RCloneServiceState.ServiceState.CheckRCloneProcess] = new()
            {
                State = RCloneServiceState.ServiceState.CheckRCloneProcess,
                OnEnter = async () => await _service._checkRCloneProcessImpl(),
                Transitions = new()
                {
                    [ServiceEvent.RCloneProcessFound] = RCloneServiceState.ServiceState.WaitStart,
                    [ServiceEvent.RCloneProcessNotFound] = RCloneServiceState.ServiceState.StartRCloneProcess,
                    [ServiceEvent.StorageReauthenticationRequired] = RCloneServiceState.ServiceState.RestartingForReauth
                }
            },
            
            [RCloneServiceState.ServiceState.StartRCloneProcess] = new()
            {
                State = RCloneServiceState.ServiceState.StartRCloneProcess,
                OnEnter = async () => await _service._startRCloneProcessImpl(),
                Transitions = new()
                {
                    [ServiceEvent.RCloneProcessStarted] = RCloneServiceState.ServiceState.WaitStart,
                    [ServiceEvent.RCloneProcessStartFailed] = RCloneServiceState.ServiceState.WaitConfig,
                    [ServiceEvent.StorageReauthenticationRequired] = RCloneServiceState.ServiceState.RestartingForReauth
                }
            },
            
            [RCloneServiceState.ServiceState.WaitStart] = new()
            {
                State = RCloneServiceState.ServiceState.WaitStart,
                OnEnter = async () => await _service._handleWaitStartImpl(),
                Transitions = new()
                {
                    [ServiceEvent.StartRequested] = RCloneServiceState.ServiceState.Running,
                    [ServiceEvent.StopRequested] = RCloneServiceState.ServiceState.WaitStop,
                    [ServiceEvent.StorageReauthenticationRequired] = RCloneServiceState.ServiceState.RestartingForReauth
                }
            },
            
            [RCloneServiceState.ServiceState.Running] = new()
            {
                State = RCloneServiceState.ServiceState.Running,
                OnEnter = async () => await _service._startRunningImpl(),
                OnExit = async () => { _service._wasUserStop = false; },
                Transitions = new()
                {
                    [ServiceEvent.StopRequested] = RCloneServiceState.ServiceState.WaitStop,
                    [ServiceEvent.StorageReauthenticationRequired] = RCloneServiceState.ServiceState.RestartingForReauth
                }
            },
            
            [RCloneServiceState.ServiceState.WaitStop] = new()
            {
                State = RCloneServiceState.ServiceState.WaitStop,
                OnEnter = async () => await _service._stopJobsImpl(),
                Transitions = new()
                {
                    [ServiceEvent.JobsCompleted] = RCloneServiceState.ServiceState.WaitStart,
                    [ServiceEvent.StartRequested] = RCloneServiceState.ServiceState.Running
                }
            },
            
            [RCloneServiceState.ServiceState.RestartingForReauth] = new()
            {
                State = RCloneServiceState.ServiceState.RestartingForReauth,
                OnEnter = async () => await _service._handleStorageReauthImpl(),
                Transitions = new()
                {
                    [ServiceEvent.ReauthCleanupComplete] = RCloneServiceState.ServiceState.BackendsLoggingIn
                }
            },
            
            [RCloneServiceState.ServiceState.Exiting] = new()
            {
                State = RCloneServiceState.ServiceState.Exiting,
                OnEnter = async () => _service._logger.LogInformation("RCloneService: Exiting."),
                Transitions = new()
            }
        };
    }
    
    public async Task TransitionAsync(ServiceEvent evt)
    {
        lock (_lock)
        {
            var currentConfig = _stateConfigs[_currentState];
            
            if (!currentConfig.Transitions.TryGetValue(evt, out var nextState))
            {
                _service._logger.LogWarning($"No transition defined for event {evt} in state {_currentState}");
                return;
            }
            
            var previousState = _currentState;
            _currentState = nextState;
            _service._state.SetState(nextState);
            
            _service._logger.LogInformation($"State transition: {previousState} -> {nextState} (event: {evt})");
        }
        
        // Notify external listeners (BackerControl) of state change
        try
        {
            _service.OnStateChanged?.Invoke(_service.GetState());
        }
        catch (Exception ex)
        {
            _service._logger.LogError(ex, "Error invoking OnStateChanged callback");
        }
        
        // Execute exit action outside of lock
        var prevConfig = _stateConfigs[_currentState];
        if (prevConfig.OnExit != null)
        {
            await prevConfig.OnExit();
        }
        
        // Execute enter action
        var nextConfig = _stateConfigs[_currentState];
        if (nextConfig.OnEnter != null)
        {
            await nextConfig.OnEnter();
        }
        
        // Process any queued events
        ServiceEvent? pendingEvent = null;
        lock (_lock)
        {
            if (_pendingEvents.TryDequeue(out var evt2))
            {
                pendingEvent = evt2;
            }
        }
        
        if (pendingEvent.HasValue)
        {
            await TransitionAsync(pendingEvent.Value);
        }
    }
    
    public void QueueEvent(ServiceEvent evt)
    {
        lock (_lock)
        {
            _pendingEvents.Enqueue(evt);
        }
    }
    
    public bool CanHandle(ServiceEvent evt)
    {
        lock (_lock)
        {
            return _stateConfigs[_currentState].Transitions.ContainsKey(evt);
        }
    }
    
    public RCloneServiceState.ServiceState CurrentState
    {
        get
        {
            lock (_lock)
            {
                return _currentState;
            }
        }
    }
}
