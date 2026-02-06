using Hannibal.Models;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services.Scheduling;

/// <summary>
/// Calculates when a rule should next execute based on its state
/// </summary>
public class ScheduleCalculator
{
    private readonly ILogger<ScheduleCalculator> _logger;
    
    // Use a far-future date instead of DateTime.MaxValue to avoid overflow issues
    // This represents "not scheduled" - 1 year from now is effectively "indefinitely"
    private static readonly TimeSpan NotScheduledDelay = TimeSpan.FromDays(365);
    
    // Default retry time if MinRetryTime is not set
    private static readonly TimeSpan DefaultRetryTime = TimeSpan.FromMinutes(15);
    
    // Default max age if MaxDestinationAge is not set
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(1);

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
            case Job.JobState.DoneWithErrors:
                // Next execution = last completion + MaxDestinationAge
                var maxAge = rule.MaxDestinationAge > TimeSpan.Zero
                    ? rule.MaxDestinationAge
                    : DefaultMaxAge;
                var nextTime = job.LastReported + maxAge;
                _logger.LogDebug(
                    "Rule {RuleId}: Last {State} at {LastReported}, next execution at {NextTime} (MaxAge: {MaxAge})",
                    rule.Id, job.State, job.LastReported, nextTime, maxAge);
                return nextTime;
            
            case Job.JobState.DoneFailure:
                // Next execution = last attempt + MinRetryTime
                var retryTime = rule.MinRetryTime > TimeSpan.Zero 
                    ? rule.MinRetryTime 
                    : DefaultRetryTime;
                var retryAt = job.LastReported + retryTime;
                _logger.LogDebug(
                    "Rule {RuleId}: Last failure at {LastReported}, retry at {RetryAt} (MinRetry: {MinRetry})",
                    rule.Id, job.LastReported, retryAt, retryTime);
                return retryAt;
            
            case Job.JobState.Executing:
            case Job.JobState.Preparing:
                // Don't schedule until current job completes - use far future but not MaxValue
                _logger.LogDebug(
                    "Rule {RuleId}: Job {JobId} still {State}, deferring schedule",
                    rule.Id, job.Id, job.State);
                return now + NotScheduledDelay;
            
            case Job.JobState.Ready:
                // Job exists but not started yet, don't create another
                _logger.LogDebug(
                    "Rule {RuleId}: Job {JobId} ready but not started, deferring schedule",
                    rule.Id, job.Id);
                return now + NotScheduledDelay;
            
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
            case Job.JobState.DoneWithErrors:
                return ScheduleReason.MaxAgeExpired;
            
            case Job.JobState.DoneFailure:
                return ScheduleReason.RetryAfterFailure;
            
            case Job.JobState.Executing:
            case Job.JobState.Preparing:
            case Job.JobState.Ready:
                return ScheduleReason.JobInProgress;
            
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
