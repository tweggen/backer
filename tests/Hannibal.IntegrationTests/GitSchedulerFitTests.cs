using FluentAssertions;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Hannibal.IntegrationTests;

/// <summary>
/// docs/plan-git-repo-storage.md Gate F - "scheduler fit": a git rule behaves
/// like any other rule under the real <see cref="RuleScheduler"/> +
/// <see cref="ScheduleCalculator"/>. This mirrors
/// <see cref="SchedulerDeterminismTests"/>'s AC2/AC3 exactly (same
/// direct-construction <see cref="RuleScheduler"/> harness, same
/// <see cref="FakeTimeProvider"/> boundary-crossing pattern, same bounded
/// polling helpers) but seeds git storages/endpoints instead of local/smb -
/// the scheduler itself (<see cref="ScheduleCalculator"/>) is technology-blind,
/// so the only thing this file adds beyond Gate 4's coverage is proof that a
/// GIT rule specifically produces the same boundary behaviour end to end
/// through the scheduler.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GitSchedulerFitTests : ApiIntegrationTestBase
{
    public GitSchedulerFitTests(PostgresFixture fixture) : base(fixture)
    {
    }

    // ------------------------------------------------------------------
    // Gate F AC1 (boundary half) - MaxDestinationAge on a GIT rule
    // (ScheduleCalculator.cs:46-54). The other half of AC1 - the fast-path
    // DoneSuccess with no data transfer on an unchanged source - is a
    // full-loop concern and lives in
    // tests/Backer.E2ETests/GitMirrorHappyPathTests.cs.
    // ------------------------------------------------------------------

    /// <summary>
    /// A git rule with <c>MaxDestinationAge = 1h</c> produces its next job at
    /// <c>LastReported + 1h</c> - no job before the boundary on the fake
    /// clock, exactly one after.
    /// </summary>
    [SkippableFact]
    public async Task AC1_GitRule_MaxDestinationAge_Boundary_ProducesExactlyOneJob_AndNoneBefore()
    {
        Fixture.SkipIfUnavailable();
        await Fixture.ResetAsync();

        var userId = $"gate-f-ac1-user-{Guid.NewGuid():N}";
        var maxAge = TimeSpan.FromHours(1);
        var t0 = DateTime.UtcNow;

        int ruleId;
        await using (var context = Fixture.CreateContext())
        {
            var (rule, sourceEp, destEp) = _buildGitRuleWithEndpoints(context, userId, "gate-f-ac1", maxAge: maxAge);
            context.Rules.Add(rule);
            await context.SaveChangesAsync();

            var oldJob = _buildJob(userId, "gate-f-ac1-seed", rule, sourceEp, destEp,
                Job.JobState.DoneSuccess, owner: "", lastReported: t0);
            context.Jobs.Add(oldJob);
            context.RuleStates.Add(new RuleState { Rule = rule, RecentJob = oldJob, ExpiredAfter = t0 + maxAge });
            await context.SaveChangesAsync();
            ruleId = rule.Id;
        }

        await using (var harness = CreateHarness(t0))
        {
            await harness.StartAsync();
            await WaitUntilAsync(
                () => Task.FromResult(harness.Scheduler.GetDependencyGraph().ContainsKey(ruleId)),
                "RuleScheduler never finished initializing the seeded git rule");

            // Before the boundary: nextExecute = t0 + 1h is strictly after
            // "now" = t0.
            (await _countJobsForRuleAsync(ruleId)).Should().Be(1,
                "no new job may exist before MaxDestinationAge has elapsed for a git rule");

            // Cross the boundary and force a deterministic pass instead of sleeping.
            harness.Clock.SetUtcNow(t0 + maxAge + TimeSpan.FromSeconds(1));
            await harness.WakeAsync();

            await WaitUntilAsync(
                async () => await _countJobsForRuleAsync(ruleId) == 2,
                "no new job appeared for the git rule after MaxDestinationAge elapsed on the fake clock");

            await using var final = Fixture.CreateContext();
            var jobs = await final.Jobs.Where(j => j.FromRuleId == ruleId).ToListAsync();
            jobs.Should().HaveCount(2, "exactly one new job, not more, must be created once ready");
            jobs.Should().ContainSingle(j => j.State == Job.JobState.Ready);
        }

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // Gate F AC2 - MinRetryTime after a guard failure on a GIT rule
    // (ScheduleCalculator.cs:56-62).
    // ------------------------------------------------------------------

    /// <summary>
    /// After a guard failure (a <c>DoneFailure</c> row with <c>Owner=""</c>
    /// and <c>LastReported</c> set - exactly the shape a guard-tripped
    /// terminal failure leaves per Gate E), the next job appears no earlier
    /// than <c>LastReported + MinRetryTime</c>. Gate 4's
    /// <c>AC3_TerminalDoneFailure</c> test already covers the generic
    /// terminal path through the real report endpoint; this test closes
    /// Gate F's wording by using a GIT rule end to end through the scheduler.
    /// </summary>
    [SkippableFact]
    public async Task AC2_GitRule_AfterGuardFailure_MinRetryTime_Boundary_ProducesExactlyOneJob_AndNoneBefore()
    {
        Fixture.SkipIfUnavailable();
        await Fixture.ResetAsync();

        var userId = $"gate-f-ac2-user-{Guid.NewGuid():N}";
        var minRetryTime = TimeSpan.FromMinutes(10);
        var t0 = DateTime.UtcNow;

        int ruleId;
        await using (var context = Fixture.CreateContext())
        {
            var (rule, sourceEp, destEp) = _buildGitRuleWithEndpoints(context, userId, "gate-f-ac2", minRetryTime: minRetryTime);
            context.Rules.Add(rule);
            await context.SaveChangesAsync();

            // Shape of a guard-tripped terminal failure (plan-git-repo-storage.md
            // §5 last bullet + Gate E AC8): State DoneFailure, Owner "",
            // LastReported set. The Terminal flag itself lives only on the
            // wire-level JobStatus DTO (see SchedulerDeterminismTests'
            // AC3_TerminalDoneFailure doc comment) - ScheduleCalculator and
            // RuleScheduler never look at it, only at State and LastReported,
            // so seeding the row directly in this shape is equivalent.
            var guardFailedJob = _buildJob(userId, "gate-f-ac2-seed", rule, sourceEp, destEp,
                Job.JobState.DoneFailure, owner: "", lastReported: t0);
            context.Jobs.Add(guardFailedJob);
            context.RuleStates.Add(new RuleState { Rule = rule, RecentJob = guardFailedJob, ExpiredAfter = t0 });
            await context.SaveChangesAsync();
            ruleId = rule.Id;
        }

        await using (var harness = CreateHarness(t0))
        {
            await harness.StartAsync();
            await WaitUntilAsync(
                () => Task.FromResult(harness.Scheduler.GetDependencyGraph().ContainsKey(ruleId)),
                "RuleScheduler never finished initializing the seeded git rule");

            (await _countJobsForRuleAsync(ruleId)).Should().Be(1,
                "no retry job may exist before MinRetryTime has elapsed since the guard-tripped failure");

            harness.Clock.SetUtcNow(t0 + minRetryTime + TimeSpan.FromSeconds(1));
            await harness.WakeAsync();

            await WaitUntilAsync(
                async () => await _countJobsForRuleAsync(ruleId) == 2,
                "no retry job appeared for the git rule after MinRetryTime elapsed on the fake clock");

            await using var final = Fixture.CreateContext();
            var jobs = await final.Jobs.Where(j => j.FromRuleId == ruleId).ToListAsync();
            jobs.Should().HaveCount(2, "exactly one retry job, not more, must be created once ready");
            jobs.Should().ContainSingle(j => j.State == Job.JobState.Ready);
        }

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // Harness - identical shape to SchedulerDeterminismTests.SchedulerHarness.
    // ------------------------------------------------------------------

    private sealed class SchedulerHarness : IAsyncDisposable
    {
        public RuleScheduler Scheduler { get; }
        public FakeTimeProvider Clock { get; }

        private readonly ServiceProvider _provider;
        private readonly CancellationTokenSource _cts = new();

        public SchedulerHarness(string connectionString, DateTime initialTime)
        {
            var services = new ServiceCollection();
            services.AddLogging(); // HannibalContext takes an ILogger<HannibalContext>
            services.AddDbContext<HannibalContext>(o => o.UseNpgsql(connectionString));
            _provider = services.BuildServiceProvider();

            Clock = new FakeTimeProvider(new DateTimeOffset(initialTime, TimeSpan.Zero));

            var calculator = new ScheduleCalculator(NullLogger<ScheduleCalculator>.Instance);
            var hub = new RecordingHubContext<Hannibal.HannibalHub>();

            Scheduler = new RuleScheduler(
                NullLogger<RuleScheduler>.Instance,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                hub,
                calculator,
                Clock);
            Scheduler.SetJobCreationEnabled(true);
        }

        public Task StartAsync() => Scheduler.StartAsync(_cts.Token);

        /// <summary>Wakes the background loop's semaphore without waiting on a real timer - a no-op event.</summary>
        public Task WakeAsync() => Scheduler.PublishEventAsync(new JobsDeletedEvent());

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await Scheduler.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected: StopAsync cancels the loop's own token.
            }

            await _provider.DisposeAsync();
            _cts.Dispose();
        }
    }

    private SchedulerHarness CreateHarness(DateTime initialTime) => new(Fixture.ConnectionString, initialTime);

    // ------------------------------------------------------------------
    // Polling helpers - identical shape to SchedulerDeterminismTests'.
    // ------------------------------------------------------------------

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(because);
    }

    private async Task<int> _countJobsForRuleAsync(int ruleId)
    {
        await using var context = Fixture.CreateContext();
        return await context.Jobs.CountAsync(j => j.FromRuleId == ruleId);
    }

    // ------------------------------------------------------------------
    // Seed helpers - same shape as SchedulerDeterminismTests', but git
    // storages/endpoints instead of local/smb.
    // ------------------------------------------------------------------

    private static (Rule rule, Endpoint sourceEndpoint, Endpoint destinationEndpoint) _buildGitRuleWithEndpoints(
        HannibalContext context, string userId, string prefix,
        TimeSpan? maxAge = null, TimeSpan? minRetryTime = null)
    {
        var source = new Storage
        {
            UserId = userId, Technology = "git", UriSchema = $"{prefix}-src-{Guid.NewGuid():N}",
            Host = "https://source.example.invalid/", IsActive = true
        };
        var destination = new Storage
        {
            UserId = userId, Technology = "git", UriSchema = $"{prefix}-dst-{Guid.NewGuid():N}",
            Host = "https://dest.example.invalid/", IsActive = true
        };
        context.Storages.AddRange(source, destination);

        var sourceEndpoint = new Endpoint
        {
            Name = $"{prefix}-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = source,
            Path = "owner/source-repo", IsActive = true
        };
        var destinationEndpoint = new Endpoint
        {
            Name = $"{prefix}-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = destination,
            Path = "owner/dest-repo", IsActive = true
        };
        context.Endpoints.AddRange(sourceEndpoint, destinationEndpoint);

        var rule = new Rule
        {
            Name = $"{prefix}-rule-{Guid.NewGuid():N}", Comment = "", UserId = userId,
            SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint,
            Operation = Rule.RuleOperation.Copy,
            MaxDestinationAge = maxAge ?? TimeSpan.Zero,
            MinRetryTime = minRetryTime ?? TimeSpan.Zero
        };

        return (rule, sourceEndpoint, destinationEndpoint);
    }

    private static Job _buildJob(
        string userId, string tagPrefix, Rule rule, Endpoint sourceEndpoint, Endpoint destinationEndpoint,
        Job.JobState state, string owner, DateTime lastReported) => new()
    {
        UserId = userId, Tag = tagPrefix, Operation = rule.Operation,
        FromRule = rule, Owner = owner, State = state,
        StartFrom = lastReported.AddMinutes(-1), EndBy = lastReported.AddDays(1), LastReported = lastReported,
        SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
    };
}
