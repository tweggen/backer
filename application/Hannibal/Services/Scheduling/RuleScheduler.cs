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
///
/// Includes dependency tracking: if Rule C reads from a path that overlaps
/// Rule A's destination, C's job creation is deferred until A's job completes.
/// </summary>
public class RuleScheduler : BackgroundService, ISchedulerEventPublisher
{
    private readonly ILogger<RuleScheduler> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IHubContext<HannibalHub> _hannibalHub;
    private readonly ScheduleCalculator _calculator;

    // Priority queue: rules sorted by next execution time
    private readonly PriorityQueue<int, DateTime> _scheduleQueue = new();

    // Fast lookup: ruleId -> scheduled rule
    private readonly Dictionary<int, ScheduledRule> _scheduledRules = new();

    // Dependency graph: ruleId -> rules that must complete before this rule can run
    private readonly Dictionary<int, HashSet<int>> _prerequisites = new();

    // Reverse dependency graph: ruleId -> rules waiting on this rule
    private readonly Dictionary<int, HashSet<int>> _dependents = new();

    // Track which rules have active jobs (Ready or Executing)
    private readonly HashSet<int> _rulesWithActiveJobs = new();

    // Anti-starvation: rules temporarily blocked from creating jobs
    // to let starved downstream rules run
    private readonly HashSet<int> _blockedForStarvation = new();

    // Event channel for external events
    private readonly Channel<SchedulerEvent> _eventChannel = Channel.CreateUnbounded<SchedulerEvent>();

    // Wakeup signal for interrupting wait
    private readonly SemaphoreSlim _wakeupSignal = new(0, 1);

    // Lock for thread-safe queue operations
    private readonly object _queueLock = new();

    // Configuration
    private bool _enableJobCreation = false;  // Start disabled, enable via config

    // Anti-starvation threshold: if a downstream rule is deferred longer than
    // 2x its MaxDestinationAge, temporarily block upstream rules
    private static readonly double AntiStarvationMultiplier = 2.0;

    // Short delay for deferred rules (re-check after 30 seconds)
    private static readonly TimeSpan DependencyDeferralDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// ISchedulerEventPublisher.IsAvailable - always true for active scheduler
    /// </summary>
    public bool IsAvailable => true;

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
                        // Cap delay to 24 hours - SemaphoreSlim.WaitAsync only accepts up to ~24.8 days
                        // and we want to wake up periodically anyway to check for changes
                        var maxDelay = TimeSpan.FromHours(24);
                        var actualDelay = delay > maxDelay ? maxDelay : delay;

                        _logger.LogDebug("Waiting {Delay} until next scheduled rule at {Time}",
                            actualDelay, nextExecuteTime.Value);

                        // Wait with timeout for next scheduled time
                        await _wakeupSignal.WaitAsync(actualDelay, cancellationToken);
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

        var rules = await context.Rules
            .Include(r => r.SourceEndpoint)
            .Include(r => r.DestinationEndpoint)
            .ToListAsync(cancellationToken);
        var ruleStates = await context.RuleStates
            .Include(rs => rs.RecentJob)
            .ToListAsync(cancellationToken);

        var stateDict = ruleStates
            .Where(rs => rs.Rule != null)
            .ToDictionary(rs => rs.Rule!.Id, rs => rs);

        // Build the dependency graph from endpoint overlap
        BuildDependencyGraph(rules);

        // Track which rules currently have active jobs
        var activeJobs = await context.Jobs
            .Where(j => j.State == Job.JobState.Ready || j.State == Job.JobState.Executing)
            .Include(j => j.FromRule)
            .ToListAsync(cancellationToken);

        lock (_queueLock)
        {
            foreach (var job in activeJobs)
            {
                if (job.FromRule != null)
                {
                    _rulesWithActiveJobs.Add(job.FromRule.Id);
                }
            }

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
    /// Build the dependency graph by detecting endpoint path overlaps between rules.
    /// Rule X depends on Rule Y if X's source endpoint overlaps Y's destination endpoint.
    /// </summary>
    internal void BuildDependencyGraph(List<Rule> rules)
    {
        lock (_queueLock)
        {
            _prerequisites.Clear();
            _dependents.Clear();

            // Initialize empty sets for all rules
            foreach (var rule in rules)
            {
                _prerequisites[rule.Id] = new HashSet<int>();
                _dependents[rule.Id] = new HashSet<int>();
            }

            // O(n^2) comparison of all rule pairs - fine for tens/hundreds of rules
            for (int i = 0; i < rules.Count; i++)
            {
                for (int j = 0; j < rules.Count; j++)
                {
                    if (i == j) continue;

                    var ruleX = rules[i]; // potential downstream (reader)
                    var ruleY = rules[j]; // potential upstream (writer)

                    // Skip if endpoints aren't loaded
                    if (ruleX.SourceEndpoint == null || ruleY.DestinationEndpoint == null)
                        continue;

                    var sourceKey = HannibalService.EndpointKey(ruleX.SourceEndpoint);
                    var destKey = HannibalService.EndpointKey(ruleY.DestinationEndpoint);

                    if (HannibalService.PathsOverlap(sourceKey, destKey))
                    {
                        // Rule X depends on Rule Y: X reads from where Y writes
                        _prerequisites[ruleX.Id].Add(ruleY.Id);
                        _dependents[ruleY.Id].Add(ruleX.Id);
                    }
                }
            }

            // Update ScheduledRule entries with dependency info
            foreach (var rule in rules)
            {
                if (_scheduledRules.TryGetValue(rule.Id, out var scheduled))
                {
                    scheduled.PrerequisiteRuleIds = _prerequisites[rule.Id];
                    scheduled.DependentRuleIds = _dependents[rule.Id];
                }
            }

            // Log detected dependencies
            foreach (var rule in rules)
            {
                var prereqs = _prerequisites[rule.Id];
                if (prereqs.Count > 0)
                {
                    var prereqNames = string.Join(", ", prereqs.Select(id =>
                    {
                        var r = rules.FirstOrDefault(r => r.Id == id);
                        return r != null ? $"Rule {id} ({r.Name})" : $"Rule {id}";
                    }));
                    _logger.LogInformation(
                        "Dependency: Rule {RuleId} ({RuleName}) depends on [{Prerequisites}]",
                        rule.Id, rule.Name, prereqNames);
                }
            }
        }
    }

    /// <summary>
    /// Check if a rule's prerequisites are satisfied (no active jobs for prerequisite rules).
    /// Also handles anti-starvation: if this rule has been deferred too long, it returns true
    /// and marks upstream rules as blocked.
    /// </summary>
    private bool AreDependenciesSatisfied(int ruleId, Rule rule)
    {
        lock (_queueLock)
        {
            if (!_prerequisites.TryGetValue(ruleId, out var prereqs) || prereqs.Count == 0)
                return true;

            // Check if this rule is blocked for anti-starvation (it's an upstream rule
            // that was blocked to let a starved downstream rule run)
            if (_blockedForStarvation.Contains(ruleId))
            {
                _logger.LogInformation(
                    "Rule {RuleId} ({RuleName}) blocked for anti-starvation, deferring",
                    ruleId, rule.Name);
                return false;
            }

            // Check if any prerequisite has an active job
            var activePrereqs = prereqs.Where(p => _rulesWithActiveJobs.Contains(p)).ToList();
            if (activePrereqs.Count == 0)
            {
                // All prerequisites satisfied - clear deferral tracking
                if (_scheduledRules.TryGetValue(ruleId, out var scheduled))
                {
                    scheduled.DependencyDeferredSince = null;
                }
                return true;
            }

            // Dependencies not satisfied - track deferral for anti-starvation
            if (_scheduledRules.TryGetValue(ruleId, out var sched))
            {
                sched.DependencyDeferredSince ??= DateTime.UtcNow;

                // Check anti-starvation threshold
                var deferredDuration = DateTime.UtcNow - sched.DependencyDeferredSince.Value;
                var threshold = rule.MaxDestinationAge > TimeSpan.Zero
                    ? rule.MaxDestinationAge * AntiStarvationMultiplier
                    : TimeSpan.FromDays(2); // default 2 days if no MaxDestinationAge

                if (deferredDuration > threshold)
                {
                    _logger.LogWarning(
                        "Anti-starvation: Rule {RuleId} ({RuleName}) deferred for {Duration}, " +
                        "blocking upstream rules {UpstreamRules}",
                        ruleId, rule.Name, deferredDuration,
                        string.Join(", ", activePrereqs));

                    // Block the upstream rules that are preventing this rule from running
                    foreach (var prereqId in activePrereqs)
                    {
                        _blockedForStarvation.Add(prereqId);
                    }

                    // Don't create the job yet - wait for the active upstream jobs to finish.
                    // Once they finish, the blocked upstream rules won't create new jobs,
                    // allowing this rule to run.
                }
            }

            _logger.LogInformation(
                "Rule {RuleId} ({RuleName}) deferred: prerequisites {ActivePrereqs} have active jobs",
                ruleId, rule.Name, string.Join(", ", activePrereqs));

            return false;
        }
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
                Reason = reason,
                PrerequisiteRuleIds = _prerequisites.GetValueOrDefault(ruleId, new HashSet<int>()),
                DependentRuleIds = _dependents.GetValueOrDefault(ruleId, new HashSet<int>()),
                DependencyDeferredSince = existing?.DependencyDeferredSince
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

            var rule = await context.Rules
                .Include(r => r.SourceEndpoint)
                .Include(r => r.DestinationEndpoint)
                .FirstOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
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

            // Check dependency gate: are all prerequisite rules idle?
            if (!AreDependenciesSatisfied(ruleId, rule))
            {
                _logger.LogInformation(
                    "Rule {RuleId} ({RuleName}) deferred due to unsatisfied dependencies",
                    ruleId, rule.Name);
                ScheduleRule(ruleId, DateTime.UtcNow + DependencyDeferralDelay,
                    ScheduleReason.DependencySatisfied);
                return;
            }

            // Create job if enabled
            if (_enableJobCreation)
            {
                await CreateJobForRuleAsync(rule, state, context, cancellationToken);

                lock (_queueLock)
                {
                    _rulesWithActiveJobs.Add(ruleId);
                }
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

            case JobsDeletedEvent jobsDeletedEvent:
                await HandleJobsDeletedAsync(jobsDeletedEvent, cancellationToken);
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
    /// Handle jobs deleted event (e.g., "Clear Jobs" button in UI)
    /// </summary>
    private async Task HandleJobsDeletedAsync(JobsDeletedEvent evt, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Jobs deleted for {Count} rules", evt.AffectedRuleIds.Count);

        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();

        // Reschedule all affected rules immediately
        foreach (var ruleId in evt.AffectedRuleIds)
        {
            var rule = await context.Rules.FindAsync(new object[] { ruleId }, cancellationToken);
            if (rule == null)
            {
                _logger.LogWarning("Rule {RuleId} not found after job deletion", ruleId);
                continue;
            }

            // Jobs were deleted, so rule state is now "no recent job" - schedule immediately
            _logger.LogInformation("Rescheduling rule {RuleId} ({RuleName}) after job deletion",
                ruleId, rule.Name);

            lock (_queueLock)
            {
                _rulesWithActiveJobs.Remove(ruleId);
                _blockedForStarvation.Remove(ruleId);
            }

            ScheduleRule(ruleId, DateTime.UtcNow, ScheduleReason.ManualTrigger);
        }

        // Also reschedule any dependent rules that may have been waiting
        RescheduleDependentRules(evt.AffectedRuleIds);

        // Wake up scheduler to process immediately
        if (evt.AffectedRuleIds.Count > 0)
        {
            try { _wakeupSignal.Release(); }
            catch (SemaphoreFullException) { }
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

        // Update active jobs tracking
        lock (_queueLock)
        {
            _rulesWithActiveJobs.Remove(evt.RuleId);

            // Clear starvation block if this was a blocked upstream rule
            _blockedForStarvation.Remove(evt.RuleId);
        }

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

        // If this rule is blocked for starvation, defer it
        bool isBlocked;
        lock (_queueLock)
        {
            isBlocked = _blockedForStarvation.Contains(evt.RuleId);
        }

        if (isBlocked)
        {
            _logger.LogInformation(
                "Rule {RuleId} ({RuleName}) completed but blocked for anti-starvation, deferring",
                evt.RuleId, rule.Name);
            // Don't reschedule immediately - wait for downstream to finish
            ScheduleRule(evt.RuleId, DateTime.UtcNow + DependencyDeferralDelay,
                ScheduleReason.DependencySatisfied);
        }
        else
        {
            ScheduleRule(evt.RuleId, nextTime, reason);
        }

        // Reschedule dependent rules that may have been waiting
        RescheduleDependentRules(new List<int> { evt.RuleId });

        // Wake up if the rule is ready now
        try { _wakeupSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Reschedule rules that depend on the given rules (which just completed or had jobs deleted).
    /// Uses ScheduleReason.DependencySatisfied.
    /// </summary>
    private void RescheduleDependentRules(List<int> completedRuleIds)
    {
        lock (_queueLock)
        {
            foreach (var completedId in completedRuleIds)
            {
                if (!_dependents.TryGetValue(completedId, out var deps))
                    continue;

                foreach (var depId in deps)
                {
                    // Check if ALL prerequisites for this dependent are now satisfied
                    if (_prerequisites.TryGetValue(depId, out var prereqs))
                    {
                        var stillBlocked = prereqs.Any(p => _rulesWithActiveJobs.Contains(p));
                        if (!stillBlocked)
                        {
                            _logger.LogInformation(
                                "Dependencies satisfied for rule {RuleId}, rescheduling with DependencySatisfied",
                                depId);

                            // Clear deferral tracking
                            if (_scheduledRules.TryGetValue(depId, out var scheduled))
                            {
                                scheduled.DependencyDeferredSince = null;
                            }

                            // Schedule immediately
                            ScheduleRule(depId, DateTime.UtcNow, ScheduleReason.DependencySatisfied);
                        }
                    }
                }
            }
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
                _rulesWithActiveJobs.Remove(evt.RuleId);
                _blockedForStarvation.Remove(evt.RuleId);
                _prerequisites.Remove(evt.RuleId);
                _dependents.Remove(evt.RuleId);

                // Remove this rule from other rules' prerequisite/dependent lists
                foreach (var prereqs in _prerequisites.Values)
                    prereqs.Remove(evt.RuleId);
                foreach (var deps in _dependents.Values)
                    deps.Remove(evt.RuleId);
            }
            return;
        }

        // Recompute dependency graph on rule changes
        await RecomputeDependencyGraphAsync(cancellationToken);

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
    /// Recompute the dependency graph from the database.
    /// Called when rules are created or updated.
    /// </summary>
    private async Task RecomputeDependencyGraphAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();

        var rules = await context.Rules
            .Include(r => r.SourceEndpoint)
            .Include(r => r.DestinationEndpoint)
            .ToListAsync(cancellationToken);

        BuildDependencyGraph(rules);
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

    /// <summary>
    /// Get the current dependency graph for diagnostics/UI.
    /// Returns a dictionary of ruleId -> prerequisite ruleIds.
    /// </summary>
    public Dictionary<int, HashSet<int>> GetDependencyGraph()
    {
        lock (_queueLock)
        {
            return _prerequisites.ToDictionary(
                kvp => kvp.Key,
                kvp => new HashSet<int>(kvp.Value));
        }
    }

    /// <summary>
    /// Get set of rules currently blocked for anti-starvation, for diagnostics.
    /// </summary>
    public HashSet<int> GetStarvationBlockedRules()
    {
        lock (_queueLock)
        {
            return new HashSet<int>(_blockedForStarvation);
        }
    }
}
