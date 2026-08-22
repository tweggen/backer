using FluentAssertions;
using Hannibal.Models;
using Hannibal.Services.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hannibal.Tests.Services.Scheduling;

/// <summary>
/// Gate E AC8 (plan-git-repo-storage.md): pins the after-failure scheduling
/// path (<c>ScheduleCalculator.cs:56-62</c>) that a terminally-failed guard
/// trip relies on for "must not spin" - the next execution is
/// <c>LastReported + MinRetryTime</c>, not "immediately" and not "never". No
/// test covered <see cref="ScheduleCalculator"/> at all before this file.
/// </summary>
public class ScheduleCalculatorTests
{
    private readonly ScheduleCalculator _calculator = new(NullLogger<ScheduleCalculator>.Instance);

    [Fact]
    public void CalculateNextExecution_AfterDoneFailure_IsLastReportedPlusMinRetryTime()
    {
        var now = DateTime.UtcNow;
        var lastReported = now.AddMinutes(-5);
        var minRetryTime = TimeSpan.FromMinutes(15);

        var rule = new Rule { Id = 1, MinRetryTime = minRetryTime };
        var job = new Job { Id = 100, State = Job.JobState.DoneFailure, LastReported = lastReported };
        var state = new RuleState { RuleId = 1, RecentJob = job };

        var next = _calculator.CalculateNextExecution(rule, state, now);

        next.Should().Be(lastReported + minRetryTime);
        // Sanity: this is what "must not spin" (AC8) actually means - the
        // rule is not eligible again until MinRetryTime has elapsed, not
        // immediately (now) and not never (NotScheduledDelay).
        next.Should().BeAfter(now, "MinRetryTime has not elapsed yet relative to LastReported");
    }

    [Fact]
    public void CalculateNextExecution_AfterDoneFailure_WithUnsetMinRetryTime_UsesTheDefault()
    {
        var now = DateTime.UtcNow;
        var lastReported = now.AddMinutes(-1);

        var rule = new Rule { Id = 2, MinRetryTime = TimeSpan.Zero };
        var job = new Job { Id = 101, State = Job.JobState.DoneFailure, LastReported = lastReported };
        var state = new RuleState { RuleId = 2, RecentJob = job };

        var next = _calculator.CalculateNextExecution(rule, state, now);

        next.Should().Be(lastReported + TimeSpan.FromMinutes(15), "the documented default retry time");
    }

    [Fact]
    public void GetScheduleReason_AfterDoneFailure_IsRetryAfterFailure()
    {
        var rule = new Rule { Id = 3, MinRetryTime = TimeSpan.FromMinutes(15) };
        var job = new Job { Id = 102, State = Job.JobState.DoneFailure, LastReported = DateTime.UtcNow };
        var state = new RuleState { RuleId = 3, RecentJob = job };

        _calculator.GetScheduleReason(rule, state).Should().Be(ScheduleReason.RetryAfterFailure);
    }

    [Fact]
    public void IsReadyToExecute_AfterDoneFailure_IsFalseBeforeMinRetryTime_TrueAfter()
    {
        var now = DateTime.UtcNow;
        var minRetryTime = TimeSpan.FromMinutes(15);
        var rule = new Rule { Id = 4, MinRetryTime = minRetryTime };
        var job = new Job { Id = 103, State = Job.JobState.DoneFailure, LastReported = now.AddMinutes(-5) };
        var state = new RuleState { RuleId = 4, RecentJob = job };

        _calculator.IsReadyToExecute(rule, state, now).Should().BeFalse(
            "only 5 of the required 15 minutes have elapsed since LastReported");
        _calculator.IsReadyToExecute(rule, state, now.AddMinutes(11)).Should().BeTrue(
            "16 minutes have now elapsed since LastReported, past MinRetryTime");
    }
}
