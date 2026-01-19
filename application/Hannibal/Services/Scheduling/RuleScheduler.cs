using System.Threading.Channels;
using Hannibal.Data;
using Hannibal.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services.Scheduling;

/// <summary>
/// Event-driven rule scheduler that replaces polling-based BackofficeService.
/// Runs alongside BackofficeService during transition period (Phase 1).
/// </summary>
public class RuleScheduler : BackgroundService
{
    private readonly ILogger<RuleScheduler> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IHubContext<HannibalHub> _hannibalHub;
    private readonly ScheduleCalculator _calculator;
    
    // Priority queue: rules sorted by next execution time
    private readonly PriorityQueue<int, DateTime> _scheduleQueue = new();
    
    // Fast lookup: ruleId → scheduled rule
    private readonly Dictionary<int, ScheduledRule> _scheduledRules = new();
    
    // Event channel for external events
    private readonly Channel<SchedulerEvent> _eventChannel = Channel.CreateUnbounded<SchedulerEvent>();
    
    // Wakeup signal for interrupting wait
    private readonly SemaphoreSlim _wakeupSignal = new(0, 1);
    
    // Lock for thread-safe queue operations
    private readonly object _queueLock = new();
    
    // Configuration
    private bool _enableJobCreation = false;  // Start disabled, enable via config
    
    public RuleScheduler(
        ILogger<RuleScheduler> logger,
        IServiceScopeFactory serviceScopeFactory,
        IHubContext<HannibalHub> hannibalHub,
        ScheduleCalculator calculator)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _hannibalHub = hannibalHub;
        _calculator = calculator;
    }

    /// <summary>
    /// Publish an event to the scheduler
    /// </summary>
    public async Task PublishEventAsync(SchedulerEvent evt)
    {
        await _eventChannel.Writer.WriteAsync(evt);
        
        // Wake up the scheduler to process the event
        try
        {
            _wakeupSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled, that's fine
        }
    }

    /// <summary>
    /// Enable or disable job creation (for gradual rollout)
    /// </summary>
    public void SetJobCreationEnabled(bool enabled)
    {
        _enableJobCreation = enabled;
        _logger.LogInformation("RuleScheduler job creation {Status}", 
            enabled ? "ENABLED" : "DISABLED");
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RuleScheduler starting (job creation DISABLED by default)");
        
        // Initial load of all rules
        await InitializeScheduleAsync(cancellationToken);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Get next scheduled time
                DateTime? nextExecuteTime;
                lock (_queueLock)
                {
                    nextExecuteTime = _scheduleQueue.TryPeek(out _, out var executeAt)
                        ? executeAt
                        : null;
                }

                // Wait until: (a) next scheduled time OR (b) external event
                if (nextExecuteTime.HasValue)
                {
                    var delay = nextExecuteTime.Value - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        _logger.LogDebug("Waiting {Delay} until next scheduled rule at {Time}", 
                            delay, nextExecuteTime.Value);
                        
                        // Wait with timeout for next scheduled time
                        await _wakeupSignal.WaitAsync(delay, cancellationToken);
                    }
                }
                else
                {
                    // No scheduled rules, wait indefinitely for event
                    _logger.LogDebug("No scheduled rules, waiting for events");
                    await _wakeupSignal.WaitAsync(cancellationToken);
                }
                
                // Process rules that are ready
                await ProcessReadyRulesAsync(cancellationToken);
                
                // Process pending events
                await ProcessPendingEventsAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduler main loop");
                await Task.Delay(1000, cancellationToken); // Brief pause before retry
            }
        }
        
        _logger.LogInformation("RuleScheduler stopped");
    }

    /// <summary>
    /// Load all rules and build initial schedule
    /// </summary>
    private async Task InitializeScheduleAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing rule schedule");
        
        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();
        
        var rules = await context.Rules.ToListAsync(cancellationToken);
        var ruleStates = await context.RuleStates
            .Include(rs => rs.RecentJob)
            .ToListAsync(cancellationToken);
        
        var stateDict = ruleStates
            .Where(rs => rs.Rule != null)
            .ToDictionary(rs => rs.Rule!.Id, rs => rs);
        
        lock (_queueLock)
        {
            foreach (var rule in rules)
            {
                stateDict.TryGetValue(rule.Id, out var state);
                var nextTime = _calculator.CalculateNextExecution(rule, state, DateTime.UtcNow);
                var reason = _calculator.GetScheduleReason(rule, state);
                
                ScheduleRule(rule.Id, nextTime, reason);
            }
        }
        
        _logger.LogInformation("Initialized schedule with {Count} rules", rules.Count);
    }

    /// <summary>
    /// Schedule a rule for execution (thread-safe)
    /// </summary>
    private void ScheduleRule(int ruleId, DateTime executeAt, ScheduleReason reason)
    {
        lock (_queueLock)
        {
            // Remove existing schedule if present
            if (_scheduledRules.TryGetValue(ruleId, out var existing))
            {
                // Can't remove from PriorityQueue easily, so we'll filter during dequeue
                _logger.LogDebug("Rescheduling rule {RuleId} from {OldTime} to {NewTime}", 
                    ruleId, existing.NextExecuteTime, executeAt);
            }
            
            var scheduled = new ScheduledRule
            {
                RuleId = ruleId,
                NextExecuteTime = executeAt,
                Reason = reason
            };
            
            _scheduledRules[ruleId] = scheduled;
            _scheduleQueue.Enqueue(ruleId, executeAt);
            
            _logger.LogDebug("Scheduled rule {RuleId} for {Time} (reason: {Reason})", 
                ruleId, executeAt, reason);
        }
    }

    /// <summary>
    /// Process all rules that are ready to execute
    /// </summary>
    private async Task ProcessReadyRulesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var readyRules = new List<int>();
        
        // Collect all ready rules
        lock (_queueLock)
        {
            while (_scheduleQueue.TryPeek(out var ruleId, out var executeAt))
            {
                if (executeAt > now)
                    break; // Queue is sorted, rest aren't ready
                
                // Dequeue this rule
                _scheduleQueue.Dequeue();
                
                // Check if this is the current schedule (not a stale entry)
                if (_scheduledRules.TryGetValue(ruleId, out var scheduled) &&
                    scheduled.NextExecuteTime == executeAt)
                {
                    readyRules.Add(ruleId);
                    _scheduledRules.Remove(ruleId); // Will be rescheduled after job creation
                }
            }
        }
        
        if (readyRules.Count > 0)
        {
            _logger.LogInformation("Processing {Count} ready rules", readyRules.Count);
            
            foreach (var ruleId in readyRules)
            {
                await ProcessRuleAsync(ruleId, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Process a single rule - create job and reschedule
    /// </summary>
    private async Task ProcessRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();
            
            var rule = await context.Rules.FindAsync(new object[] { ruleId }, cancellationToken);
            if (rule == null)
            {
                _logger.LogWarning("Rule {RuleId} not found, removing from schedule", ruleId);
                return;
            }
            
            var state = await context.RuleStates
                .Include(rs => rs.RecentJob)
                .FirstOrDefaultAsync(rs => rs.Rule!.Id == ruleId, cancellationToken);
            
            // Verify rule is still ready (double-check)
            if (!_calculator.IsReadyToExecute(rule, state, DateTime.UtcNow))
            {
                _logger.LogDebug("Rule {RuleId} no longer ready, rescheduling", ruleId);
                var nextTime = _calculator.CalculateNextExecution(rule, state, DateTime.UtcNow);
                var reason = _calculator.GetScheduleReason(rule, state);
                ScheduleRule(ruleId, nextTime, reason);
                return;
            }
            
            // Create job if enabled
            if (_enableJobCreation)
            {
                await CreateJobForRuleAsync(rule, state, context, cancellationToken);
            }
            else
            {
                _logger.LogInformation(
                    "[DRY-RUN] Would create job for rule {RuleId} ({RuleName})", 
                    ruleId, rule.Name);
            }
            
            // Reschedule for next execution
            var newNextTime = _calculator.CalculateNextExecution(rule, state, DateTime.UtcNow);
            var newReason = _calculator.GetScheduleReason(rule, state);
            ScheduleRule(ruleId, newNextTime, newReason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing rule {RuleId}", ruleId);
        }
    }

    /// <summary>
    /// Create a job for a rule
    /// </summary>
    private async Task CreateJobForRuleAsync(
        Rule rule, 
        RuleState? state, 
        HannibalContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        
        var job = new Job
        {
            Tag = $"[Scheduler] {rule.Name}",
            UserId = rule.UserId,
            FromRule = rule,
            Operation = rule.Operation,
            Owner = "",
            State = Job.JobState.Ready,
            StartFrom = now,
            EndBy = now + TimeSpan.FromDays(1),
            SourceEndpoint = rule.SourceEndpoint,
            DestinationEndpoint = rule.DestinationEndpoint,
            Status = 0
        };
        
        await context.Jobs.AddAsync(job, cancellationToken);
        
        // Update or create rule state
        if (state == null)
        {
            state = new RuleState
            {
                Rule = rule,
                ExpiredAfter = now + rule.MaxDestinationAge,
                RecentJob = job
            };
            await context.RuleStates.AddAsync(state, cancellationToken);
        }
        else
        {
            state.ExpiredAfter = now + rule.MaxDestinationAge;
            state.RecentJob = job;
        }
        
        await context.SaveChangesAsync(cancellationToken);
        
        _logger.LogInformation(
            "Created job {JobId} for rule {RuleId} ({RuleName})", 
            job.Id, rule.Id, rule.Name);
        
        // Notify agents
        await _hannibalHub.Clients.All.SendAsync("NewJobAvailable", cancellationToken);
    }

    /// <summary>
    /// Process all pending events from the channel
    /// </summary>
    private async Task ProcessPendingEventsAsync(CancellationToken cancellationToken)
    {
        // Process all available events (non-blocking)
        while (_eventChannel.Reader.TryRead(out var evt))
        {
            try
            {
                await ProcessEventAsync(evt, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing event {EventType}", evt.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Process a single event
    /// </summary>
    private async Task ProcessEventAsync(SchedulerEvent evt, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing event {EventType}", evt.GetType().Name);
        
        switch (evt)
        {
            case JobCompletedEvent jobEvent:
                await HandleJobCompletedAsync(jobEvent, cancellationToken);
                break;
            
            case RuleChangedEvent ruleEvent:
                await HandleRuleChangedAsync(ruleEvent, cancellationToken);
                break;
            
            case ManualTriggerEvent triggerEvent:
                await HandleManualTriggerAsync(triggerEvent, cancellationToken);
                break;
            
            default:
                _logger.LogWarning("Unknown event type: {Type}", evt.GetType().Name);
                break;
        }
    }

    /// <summary>
    /// Handle job completion event
    /// </summary>
    private async Task HandleJobCompletedAsync(JobCompletedEvent evt, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Job {JobId} completed with state {State} for rule {RuleId}", 
            evt.JobId, evt.FinalState, evt.RuleId);
        
        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();
        
        var rule = await context.Rules.FindAsync(new object[] { evt.RuleId }, cancellationToken);
        if (rule == null)
        {
            _logger.LogWarning("Rule {RuleId} not found for completed job {JobId}", 
                evt.RuleId, evt.JobId);
            return;
        }
        
        var state = await context.RuleStates
            .Include(rs => rs.RecentJob)
            .FirstOrDefaultAsync(rs => rs.Rule!.Id == evt.RuleId, cancellationToken);
        
        // Recalculate next execution time based on completion
        var nextTime = _calculator.CalculateNextExecution(rule, state, DateTime.UtcNow);
        var reason = _calculator.GetScheduleReason(rule, state);
        
        ScheduleRule(evt.RuleId, nextTime, reason);
        
        // Wake up if the rule is ready now
        if (nextTime <= DateTime.UtcNow)
        {
            try { _wakeupSignal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    /// <summary>
    /// Handle rule changed event
    /// </summary>
    private async Task HandleRuleChangedAsync(RuleChangedEvent evt, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Rule {RuleId} {ChangeType}", evt.RuleId, evt.ChangeType);
        
        if (evt.ChangeType == RuleChangeType.Deleted)
        {
            lock (_queueLock)
            {
                _scheduledRules.Remove(evt.RuleId);
            }
            return;
        }
        
        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();
        
        var rule = await context.Rules.FindAsync(new object[] { evt.RuleId }, cancellationToken);
        if (rule == null)
        {
            _logger.LogWarning("Rule {RuleId} not found", evt.RuleId);
            return;
        }
        
        var state = await context.RuleStates
            .Include(rs => rs.RecentJob)
            .FirstOrDefaultAsync(rs => rs.Rule!.Id == evt.RuleId, cancellationToken);
        
        var nextTime = _calculator.CalculateNextExecution(rule, state, DateTime.UtcNow);
        var reason = evt.ChangeType == RuleChangeType.Created 
            ? ScheduleReason.InitialSchedule 
            : ScheduleReason.RuleModified;
        
        ScheduleRule(evt.RuleId, nextTime, reason);
        
        // Wake up if ready now
        if (nextTime <= DateTime.UtcNow)
        {
            try { _wakeupSignal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    /// <summary>
    /// Handle manual trigger event
    /// </summary>
    private async Task HandleManualTriggerAsync(ManualTriggerEvent evt, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manual trigger for rule {RuleId}", evt.RuleId);
        
        // Schedule immediately
        ScheduleRule(evt.RuleId, DateTime.UtcNow, ScheduleReason.ManualTrigger);
        
        // Wake up scheduler
        try { _wakeupSignal.Release(); }
        catch (SemaphoreFullException) { }
    }
}
