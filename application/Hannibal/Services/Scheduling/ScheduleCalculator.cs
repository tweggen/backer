using Hannibal.Models;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services.Scheduling;

/// <summary>
/// Calculates when a rule should next execute based on its state
/// </summary>
public class ScheduleCalculator
{
    private readonly ILogger<ScheduleCalculator> _logger;

    public ScheduleCalculator(ILogger<ScheduleCalculator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculate the next execution time for a rule
    /// </summary>
    public DateTime CalculateNextExecution(Rule rule, RuleState? state, DateTime now)
    {
        // If no state exists or no recent job, schedule immediately
        if (state?.RecentJob == null)
        {
            _logger.LogDebug("Rule {RuleId}: No recent job, scheduling immediately", rule.Id);
            return now;
        }

        var job = state.RecentJob;
        
        switch (job.State)
        {
            case Job.JobState.DoneSuccess:
                // Next execution = last completion + MaxDestinationAge
                var nextTime = job.LastReported + rule.MaxDestinationAge;
                _logger.LogDebug(
                    "Rule {RuleId}: Last success at {LastReported}, next execution at {NextTime} (MaxAge: {MaxAge})",
                    rule.Id, job.LastReported, nextTime, rule.MaxDestinationAge);
                return nextTime;
            
            case Job.JobState.DoneFailure:
                // Next execution = last attempt + MinRetryTime
                var retryTime = job.LastReported + rule.MinRetryTime;
                _logger.LogDebug(
                    "Rule {RuleId}: Last failure at {LastReported}, retry at {RetryTime} (MinRetry: {MinRetry})",
                    rule.Id, job.LastReported, retryTime, rule.MinRetryTime);
                return retryTime;
            
            case Job.JobState.Executing:
            case Job.JobState.Preparing:
                // Don't schedule until current job completes
                _logger.LogDebug(
                    "Rule {RuleId}: Job {JobId} still {State}, not scheduling",
                    rule.Id, job.Id, job.State);
                return DateTime.MaxValue;
            
            case Job.JobState.Ready:
                // Job exists but not started yet, don't create another
                _logger.LogDebug(
                    "Rule {RuleId}: Job {JobId} ready but not started, not scheduling",
                    rule.Id, job.Id);
                return DateTime.MaxValue;
            
            default:
                _logger.LogWarning(
                    "Rule {RuleId}: Unknown job state {State}, scheduling immediately",
                    rule.Id, job.State);
                return now;
        }
    }

    /// <summary>
    /// Determine why a rule is being scheduled
    /// </summary>
    public ScheduleReason GetScheduleReason(Rule rule, RuleState? state)
    {
        if (state?.RecentJob == null)
            return ScheduleReason.InitialSchedule;
        
        switch (state.RecentJob.State)
        {
            case Job.JobState.DoneSuccess:
                return ScheduleReason.MaxAgeExpired;
            
            case Job.JobState.DoneFailure:
                return ScheduleReason.RetryAfterFailure;
            
            default:
                return ScheduleReason.InitialSchedule;
        }
    }

    /// <summary>
    /// Check if a rule is currently ready to execute
    /// </summary>
    public bool IsReadyToExecute(Rule rule, RuleState? state, DateTime now)
    {
        var nextTime = CalculateNextExecution(rule, state, now);
        return nextTime <= now;
    }
}
