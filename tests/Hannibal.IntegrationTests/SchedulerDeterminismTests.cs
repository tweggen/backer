using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Hannibal.IntegrationTests;

/// <summary>
/// docs/plan-e2e-test-harness.md Gate 4 - Scheduler determinism.
///
/// <see cref="RuleScheduler"/> now takes a <see cref="TimeProvider"/> (all
/// seventeen former <c>DateTime.UtcNow</c> reads go through it - the plan text
/// says ten, an earlier count; the current file has seventeen, all replaced).
/// These tests construct a <see cref="RuleScheduler"/> directly - not through
/// <see cref="TestSupport.Api.BackerApiFactory"/> - against a real
/// <see cref="HannibalContext"/> backed by the throwaway PostgreSQL database, a
/// <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>, and a
/// <see cref="TestSupport.Api.RecordingHubContext{THub}"/>. This is the
/// "direct-construction" route the plan calls out: <see cref="RuleScheduler"/>
/// only depends on <c>ILogger</c>, <c>IServiceScopeFactory</c>,
/// <c>IHubContext&lt;HannibalHub&gt;</c> and <see cref="ScheduleCalculator"/>,
/// all of which are cheap to build without the whole API host, and it gives
/// real determinism: advance the fake clock, publish an event to force a
/// deterministic pass, poll DB state with a generous bounded ceiling - never a
/// raw <c>Task.Delay</c> as synchronisation.
///
/// A structural note the tests below rely on: <see cref="RuleScheduler"/>'s
/// background loop only ever performs a real (wall-clock) wait when its
/// internal <c>_wakeupSignal.WaitAsync(delay, ct)</c> has nothing to wake it -
/// and that delay is computed from the *fake* clock. Seeding a rule whose next
/// execution is far in fake-clock future means the loop's real wait is capped
/// at up to 24h of *wall* time, so nothing can possibly run again before this
/// test process asks it to - "no job before the boundary" is therefore a
/// structural guarantee, not a race, once <see cref="SchedulerHarness"/> has
/// finished initializing (observed through the public
/// <see cref="RuleScheduler.GetDependencyGraph"/>).
/// </summary>
[Collection(PostgresCollection.Name)]
public class SchedulerDeterminismTests : ApiIntegrationTestBase
{
    private const string ReportRoute = "/api/hannibal/v1/reportJob";

    public SchedulerDeterminismTests(PostgresFixture fixture) : base(fixture)
    {
    }

    // ------------------------------------------------------------------
    // AC2 - MaxDestinationAge boundary
    // ------------------------------------------------------------------

    /// <summary>
    /// AC2: a rule whose last job completed <c>MaxDestinationAge</c> ago
    /// produces exactly one new job when the fake clock advances past the
    /// boundary - and none before (<c>ScheduleCalculator.cs:46-54</c>).
    /// </summary>
    [SkippableFact]
    public async Task AC2_MaxDestinationAge_Boundary_ProducesExactlyOneJob_AndNoneBefore()
    {
        Fixture.SkipIfUnavailable();
        await Fixture.ResetAsync();

        var userId = $"ac2-user-{Guid.NewGuid():N}";
        var maxAge = TimeSpan.FromMinutes(30);
        var t0 = DateTime.UtcNow;

        int ruleId;
        await using (var context = Fixture.CreateContext())
        {
            var (rule, sourceEp, destEp) = _buildRuleWithEndpoints(context, userId, "ac2", maxAge: maxAge);
            context.Rules.Add(rule);
            await context.SaveChangesAsync();

            var oldJob = _buildJob(userId, "ac2-seed", rule, sourceEp, destEp,
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
                "RuleScheduler never finished initializing the seeded rule");

            // Before the boundary: nextExecute = t0 + maxAge is strictly after
            // "now" = t0. See the class doc comment for why this is a structural
            // guarantee, not a race.
            (await _countJobsForRuleAsync(ruleId)).Should().Be(1,
                "no new job may exist before MaxDestinationAge has elapsed");

            // Cross the boundary and force a deterministic pass instead of sleeping.
            harness.Clock.SetUtcNow(t0 + maxAge + TimeSpan.FromSeconds(1));
            await harness.WakeAsync();

            await WaitUntilAsync(
                async () => await _countJobsForRuleAsync(ruleId) == 2,
                "no new job appeared after MaxDestinationAge elapsed on the fake clock");

            await using var final = Fixture.CreateContext();
            var jobs = await final.Jobs.Where(j => j.FromRuleId == ruleId).ToListAsync();
            jobs.Should().HaveCount(2, "exactly one new job, not more, must be created once ready");
            jobs.Should().ContainSingle(j => j.State == Job.JobState.Ready);
        }

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // AC3 - MinRetryTime boundary after DoneFailure
    // ------------------------------------------------------------------

    /// <summary>
    /// AC3: after a <c>DoneFailure</c>, no job is created before
    /// <c>LastReported + MinRetryTime</c> and exactly one after
    /// (<c>ScheduleCalculator.cs:56-62</c>).
    /// </summary>
    [SkippableFact]
    public async Task AC3_MinRetryTime_Boundary_ProducesExactlyOneJob_AndNoneBefore()
    {
        Fixture.SkipIfUnavailable();
        await Fixture.ResetAsync();

        var userId = $"ac3-user-{Guid.NewGuid():N}";
        var minRetryTime = TimeSpan.FromMinutes(10);
        var t0 = DateTime.UtcNow;

        int ruleId;
        await using (var context = Fixture.CreateContext())
        {
            var (rule, sourceEp, destEp) = _buildRuleWithEndpoints(context, userId, "ac3", minRetryTime: minRetryTime);
            context.Rules.Add(rule);
            await context.SaveChangesAsync();

            var failedJob = _buildJob(userId, "ac3-seed", rule, sourceEp, destEp,
                Job.JobState.DoneFailure, owner: "", lastReported: t0);
            context.Jobs.Add(failedJob);
            context.RuleStates.Add(new RuleState { Rule = rule, RecentJob = failedJob, ExpiredAfter = t0 });
            await context.SaveChangesAsync();
            ruleId = rule.Id;
        }

        await using (var harness = CreateHarness(t0))
        {
            await harness.StartAsync();
            await WaitUntilAsync(
                () => Task.FromResult(harness.Scheduler.GetDependencyGraph().ContainsKey(ruleId)),
                "RuleScheduler never finished initializing the seeded rule");

            (await _countJobsForRuleAsync(ruleId)).Should().Be(1,
                "no retry job may exist before MinRetryTime has elapsed since the failure");

            harness.Clock.SetUtcNow(t0 + minRetryTime + TimeSpan.FromSeconds(1));
            await harness.WakeAsync();

            await WaitUntilAsync(
                async () => await _countJobsForRuleAsync(ruleId) == 2,
                "no retry job appeared after MinRetryTime elapsed on the fake clock");

            await using var final = Fixture.CreateContext();
            var jobs = await final.Jobs.Where(j => j.FromRuleId == ruleId).ToListAsync();
            jobs.Should().HaveCount(2, "exactly one retry job, not more, must be created once ready");
            jobs.Should().ContainSingle(j => j.State == Job.JobState.Ready);
        }

        await Fixture.ResetAsync();
    }

    /// <summary>
    /// AC3 addendum: a Gate E "terminal" <c>DoneFailure</c> (guard-tripped,
    /// <c>JobStatus.Terminal = true</c>, produced through the real report
    /// endpoint rather than hand-seeded) is scheduled identically to an
    /// ordinary <c>DoneFailure</c>. <see cref="ScheduleCalculator"/> and
    /// <see cref="RuleScheduler"/> only ever look at <c>Job.State</c> and
    /// <c>Job.LastReported</c> - the <c>Terminal</c> flag exists only on the
    /// wire-level <see cref="JobStatus"/> DTO consumed by
    /// <c>HannibalServiceJobs.ReportJobAsync</c>, and is not a persisted
    /// column - so this proves Gate E's terminal marking and Gate 4's clock
    /// seam compose, without needing any Terminal-aware branch here.
    /// </summary>
    [SkippableFact]
    public async Task AC3_TerminalDoneFailure_IsScheduledTheSameAsAnOrdinaryFailure()
    {
        await ArrangeAsync();
        var email = UniqueEmail("ac3-terminal");
        using var client = await CreateAuthenticatedClientAsync(email, "Passw0rd!");
        var userId = await _userIdAsync(email);

        var minRetryTime = TimeSpan.FromMinutes(5);
        int ruleId;
        int jobId;
        await using (var context = Fixture.CreateContext())
        {
            var (rule, sourceEp, destEp) = _buildRuleWithEndpoints(context, userId, "ac3-terminal", minRetryTime: minRetryTime);
            context.Rules.Add(rule);
            await context.SaveChangesAsync();

            var executingJob = _buildJob(userId, "ac3-terminal-seed", rule, sourceEp, destEp,
                Job.JobState.Executing, owner: "guard-agent", lastReported: DateTime.UtcNow);
            context.Jobs.Add(executingJob);
            await context.SaveChangesAsync();
            ruleId = rule.Id;
            jobId = executingJob.Id;
        }

        var report = await client.PostAsJsonAsync(ReportRoute, new JobStatus
        {
            JobId = jobId, Owner = "guard-agent", State = Job.JobState.DoneFailure, Terminal = true
        });
        report.EnsureSuccessStatusCode();

        DateTime lastReported;
        await using (var context = Fixture.CreateContext())
        {
            // Tracked (not AsNoTracking): RuleState.RecentJob needs the actual
            // entity, not just its id - RuleState has no RecentJobId setter.
            var row = await context.Jobs.FirstAsync(j => j.Id == jobId);
            row.State.Should().Be(Job.JobState.DoneFailure, "the guard trip must have recorded a terminal failure");
            lastReported = row.LastReported;

            // The scheduler learns about the terminally-failed job the same
            // way production does: through RuleState.RecentJob.
            context.RuleStates.Add(new RuleState { RuleId = ruleId, RecentJob = row, ExpiredAfter = lastReported });
            await context.SaveChangesAsync();
        }

        await using (var harness = CreateHarness(lastReported))
        {
            await harness.StartAsync();
            await WaitUntilAsync(
                () => Task.FromResult(harness.Scheduler.GetDependencyGraph().ContainsKey(ruleId)),
                "RuleScheduler never finished initializing the seeded rule");

            (await _countJobsForRuleAsync(ruleId)).Should().Be(1,
                "no retry job may exist before MinRetryTime has elapsed since the terminal failure");

            harness.Clock.SetUtcNow(lastReported + minRetryTime + TimeSpan.FromSeconds(1));
            await harness.WakeAsync();

            await WaitUntilAsync(
                async () => await _countJobsForRuleAsync(ruleId) == 2,
                "no retry job appeared after MinRetryTime elapsed following the terminal failure");
        }

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // AC4 - event path picks up a rule created after the scheduler started
    // ------------------------------------------------------------------

    /// <summary>
    /// AC4: a rule created *after* the scheduler started is picked up via
    /// the event path (<c>RuleScheduler.PublishEventAsync</c>), not only at
    /// <c>InitializeScheduleAsync</c> - the database is empty when the
    /// scheduler starts, so the only way it can ever learn about the rule
    /// is <c>RuleChangedEvent</c>.
    /// </summary>
    [SkippableFact]
    public async Task AC4_RuleCreatedAfterSchedulerStarted_IsPickedUpViaEventPath()
    {
        Fixture.SkipIfUnavailable();
        await Fixture.ResetAsync();

        var t0 = DateTime.UtcNow;
        await using (var harness = CreateHarness(t0))
        {
            await harness.StartAsync();

            var userId = $"ac4-user-{Guid.NewGuid():N}";
            int ruleId;
            await using (var context = Fixture.CreateContext())
            {
                var (rule, _, _) = _buildRuleWithEndpoints(context, userId, "ac4");
                context.Rules.Add(rule);
                await context.SaveChangesAsync();
                ruleId = rule.Id;
            }

            await harness.Scheduler.PublishEventAsync(
                new RuleChangedEvent { RuleId = ruleId, ChangeType = RuleChangeType.Created });

            await WaitUntilAsync(
                async () => await _countJobsForRuleAsync(ruleId) == 1,
                "a rule created after the scheduler started was never picked up via the event path");

            await using var final = Fixture.CreateContext();
            var jobs = await final.Jobs.Where(j => j.FromRuleId == ruleId).ToListAsync();
            jobs.Should().ContainSingle().Which.State.Should().Be(Job.JobState.Ready);
        }

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // AC5 - no duplicate jobs
    // ------------------------------------------------------------------

    /// <summary>
    /// AC5: no duplicate jobs - the same rule triggered twice back to back
    /// (two <c>ManualTriggerEvent</c>s, which unconditionally reschedule the
    /// rule for "now" regardless of state) yields exactly ONE job row. The
    /// second trigger's re-scheduling is caught by <c>ProcessRuleAsync</c>'s
    /// own double-check against freshly loaded <c>RuleState</c>
    /// (<c>IsReadyToExecute</c>, RuleScheduler.cs:475) once the first job
    /// exists.
    /// </summary>
    [SkippableFact]
    public async Task AC5_RuleTriggeredTwice_YieldsExactlyOneJobRow()
    {
        Fixture.SkipIfUnavailable();
        await Fixture.ResetAsync();

        var userId = $"ac5-user-{Guid.NewGuid():N}";
        var t0 = DateTime.UtcNow;

        int ruleId;
        await using (var context = Fixture.CreateContext())
        {
            var (rule, _, _) = _buildRuleWithEndpoints(context, userId, "ac5");
            context.Rules.Add(rule);
            await context.SaveChangesAsync();
            ruleId = rule.Id;
        }

        await using (var harness = CreateHarness(t0))
        {
            await harness.StartAsync();
            await WaitUntilAsync(
                () => Task.FromResult(harness.Scheduler.GetDependencyGraph().ContainsKey(ruleId)),
                "RuleScheduler never finished initializing the seeded rule");

            await harness.Scheduler.PublishEventAsync(new ManualTriggerEvent { RuleId = ruleId });
            await harness.Scheduler.PublishEventAsync(new ManualTriggerEvent { RuleId = ruleId });

            await WaitUntilAsync(
                async () => await _countJobsForRuleAsync(ruleId) >= 1,
                "neither manual trigger ever produced a job");

            // Settle window: the second trigger's redundant re-schedule is
            // resolved by an additional zero-real-wait loop pass (delay <= 0, no
            // semaphore wait involved) - confirm it never sneaks a second job in.
            await _assertJobCountStaysAsync(ruleId, expected: 1, window: TimeSpan.FromMilliseconds(500));
        }

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // AC6 - anti-starvation and dependency-deferral paths
    // ------------------------------------------------------------------

    /// <summary>
    /// AC6 (anti-starvation half): a rule deferred by an active prerequisite
    /// must not create a job while the prerequisite is active. This test
    /// also pins an actual-behaviour finding: <c>ProcessReadyRulesAsync</c>
    /// removes a ready rule's <see cref="ScheduledRule"/> entry from
    /// <c>_scheduledRules</c> (RuleScheduler.cs:434) *before* calling
    /// <c>ProcessRuleAsync</c>, which is the only caller of
    /// <c>AreDependenciesSatisfied</c> (RuleScheduler.cs:485). Because that
    /// method's <c>DependencyDeferredSince</c> stamp and anti-starvation
    /// escalation (RuleScheduler.cs:336-364) are gated behind
    /// <c>_scheduledRules.TryGetValue(ruleId, ...)</c>, and the entry was
    /// just removed, that lookup always misses on the only call path that
    /// reaches it - <c>DependencyDeferredSince</c> is never stamped and the
    /// anti-starvation block never engages, no matter how long a rule stays
    /// deferred. That is pinned here (well past 2 x MaxDestinationAge, the
    /// documented threshold) rather than "fixed": the task is a seam
    /// injection, and changing scheduling semantics is explicitly out of
    /// scope. See the final report for the write-up.
    /// </summary>
    [SkippableFact]
    public async Task AC6_AntiStarvation_DeferredRuleWithActiveUpstream_NeverCreatesJob()
    {
        Fixture.SkipIfUnavailable();
        await Fixture.ResetAsync();

        var userId = $"ac6a-user-{Guid.NewGuid():N}";
        var maxAge = TimeSpan.FromSeconds(2); // anti-starvation threshold = 2 x this = 4s
        var t0 = DateTime.UtcNow;

        int ruleAId, ruleCId;
        await using (var context = Fixture.CreateContext())
        {
            var (ruleA, ruleC) = _buildDependentRulePair(context, userId, "ac6a", maxAge);
            context.Rules.AddRange(ruleA, ruleC);
            await context.SaveChangesAsync();

            var jobA = _buildJob(userId, "ac6a-upstream", ruleA, ruleA.SourceEndpoint, ruleA.DestinationEndpoint,
                Job.JobState.Executing, owner: "busy-agent", lastReported: t0);
            context.Jobs.Add(jobA);
            context.RuleStates.Add(new RuleState { Rule = ruleA, RecentJob = jobA, ExpiredAfter = t0 + maxAge });
            // ruleC has no RuleState: a fresh rule, InitialSchedule = ready immediately.
            await context.SaveChangesAsync();
            ruleAId = ruleA.Id;
            ruleCId = ruleC.Id;
        }

        await using (var harness = CreateHarness(t0))
        {
            await harness.StartAsync();
            await WaitUntilAsync(
                () => Task.FromResult(harness.Scheduler.GetDependencyGraph().ContainsKey(ruleCId)
                                       && harness.Scheduler.GetDependencyGraph()[ruleCId].Contains(ruleAId)),
                "RuleScheduler never built the A->C dependency");

            // C is ready immediately (InitialSchedule) but deferred because A is
            // active; confirm it does not create a job on the very first pass.
            await _assertJobCountStaysAsync(ruleCId, expected: 0, window: TimeSpan.FromMilliseconds(500));

            // Jump the fake clock well past the anti-starvation threshold and
            // force another pass.
            harness.Clock.Advance(TimeSpan.FromMinutes(10));
            await harness.WakeAsync();

            await _assertJobCountStaysAsync(ruleCId, expected: 0, window: TimeSpan.FromSeconds(2));
            harness.Scheduler.GetStarvationBlockedRules().Should().BeEmpty(
                "the anti-starvation escalation is unreachable given the current removal-before-check " +
                "ordering in ProcessReadyRulesAsync - see the test's doc comment");
        }

        await Fixture.ResetAsync();
    }

    /// <summary>
    /// AC6 (dependency-satisfied half): a dependent rule schedules - and
    /// gets a job - once its prerequisite's job completes
    /// (<c>RescheduleDependentRules</c>, RuleScheduler.cs:770, reached from
    /// <c>HandleJobCompletedAsync</c>). Unlike the anti-starvation half
    /// above, this path does not go through <c>AreDependenciesSatisfied</c>'s
    /// broken stamping and works exactly as designed.
    /// </summary>
    [SkippableFact]
    public async Task AC6_DependencySatisfied_DependentRuleSchedulesAfterPrerequisiteCompletes()
    {
        Fixture.SkipIfUnavailable();
        await Fixture.ResetAsync();

        var userId = $"ac6b-user-{Guid.NewGuid():N}";
        var maxAge = TimeSpan.FromMinutes(30);
        var t0 = DateTime.UtcNow;

        int ruleAId, ruleCId, jobAId;
        await using (var context = Fixture.CreateContext())
        {
            var (ruleA, ruleC) = _buildDependentRulePair(context, userId, "ac6b", maxAge);
            context.Rules.AddRange(ruleA, ruleC);
            await context.SaveChangesAsync();

            var jobA = _buildJob(userId, "ac6b-upstream", ruleA, ruleA.SourceEndpoint, ruleA.DestinationEndpoint,
                Job.JobState.Executing, owner: "busy-agent", lastReported: t0);
            context.Jobs.Add(jobA);
            context.RuleStates.Add(new RuleState { Rule = ruleA, RecentJob = jobA, ExpiredAfter = t0 + maxAge });
            await context.SaveChangesAsync();
            ruleAId = ruleA.Id;
            ruleCId = ruleC.Id;
            jobAId = jobA.Id;
        }

        await using (var harness = CreateHarness(t0))
        {
            await harness.StartAsync();
            await WaitUntilAsync(
                () => Task.FromResult(harness.Scheduler.GetDependencyGraph().ContainsKey(ruleCId)
                                       && harness.Scheduler.GetDependencyGraph()[ruleCId].Contains(ruleAId)),
                "RuleScheduler never built the A->C dependency");

            // Confirm C really is deferred first, so "schedules after completion"
            // is a meaningful claim and not something that would have happened
            // anyway.
            await _assertJobCountStaysAsync(ruleCId, expected: 0, window: TimeSpan.FromMilliseconds(500));

            await harness.Scheduler.PublishEventAsync(new JobCompletedEvent
            {
                JobId = jobAId, RuleId = ruleAId, FinalState = Job.JobState.DoneSuccess
            });

            await WaitUntilAsync(
                async () => await _countJobsForRuleAsync(ruleCId) == 1,
                "the dependent rule was never scheduled after its prerequisite's job completed");

            await using var final = Fixture.CreateContext();
            var jobs = await final.Jobs.Where(j => j.FromRuleId == ruleCId).ToListAsync();
            jobs.Should().ContainSingle().Which.State.Should().Be(Job.JobState.Ready);
        }

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    /// <summary>
    /// Direct-construction harness for <see cref="RuleScheduler"/>: a real
    /// <see cref="HannibalContext"/> DI container against the throwaway
    /// database (via its own <see cref="IServiceScopeFactory"/>, exactly the
    /// dependency shape <see cref="RuleScheduler"/> asks for), a
    /// <see cref="RecordingHubContext{THub}"/> in place of SignalR, and a
    /// <see cref="FakeTimeProvider"/> in place of <see cref="TimeProvider.System"/>.
    /// No <see cref="TestSupport.Api.BackerApiFactory"/> involved - this is
    /// the "direct construction" route, chosen because it gives real
    /// determinism (advance the fake clock, force a pass, no background
    /// timing) without the cost of hosting the whole API.
    /// </summary>
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
    // Polling helpers - never a raw Task.Delay as synchronisation; a
    // condition polled with a generous, bounded ceiling.
    // ------------------------------------------------------------------

    /// <summary>A ceiling on a condition, not a sleep: returns as soon as it holds.</summary>
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

    /// <summary>
    /// Repeatedly asserts a count stays at <paramref name="expected"/> for
    /// the given bounded window, failing immediately (not just at the end)
    /// if a violation appears. Used for negative-space assertions ("nothing
    /// more happens") which cannot be expressed as "wait for a condition to
    /// become true".
    /// </summary>
    private async Task _assertJobCountStaysAsync(int ruleId, int expected, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            (await _countJobsForRuleAsync(ruleId)).Should().Be(expected,
                "no job may be created outside of the window this test is pinning");
            await Task.Delay(25);
        }
    }

    private async Task<int> _countJobsForRuleAsync(int ruleId)
    {
        await using var context = Fixture.CreateContext();
        return await context.Jobs.CountAsync(j => j.FromRuleId == ruleId);
    }

    private async Task<string> _userIdAsync(string email)
    {
        using var scope = Api.Services.CreateScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull();
        return user!.Id;
    }

    // ------------------------------------------------------------------
    // Seed helpers
    // ------------------------------------------------------------------

    private static (Rule rule, Endpoint sourceEndpoint, Endpoint destinationEndpoint) _buildRuleWithEndpoints(
        HannibalContext context, string userId, string prefix,
        TimeSpan? maxAge = null, TimeSpan? minRetryTime = null)
    {
        var source = new Storage
        {
            UserId = userId, Technology = "local", UriSchema = $"{prefix}-src-{Guid.NewGuid():N}", IsActive = true
        };
        var destination = new Storage
        {
            UserId = userId, Technology = "smb", UriSchema = $"{prefix}-dst-{Guid.NewGuid():N}", IsActive = true
        };
        context.Storages.AddRange(source, destination);

        var sourceEndpoint = new Endpoint
        {
            Name = $"{prefix}-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = source,
            Path = "/source", IsActive = true
        };
        var destinationEndpoint = new Endpoint
        {
            Name = $"{prefix}-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = destination,
            Path = "/destination", IsActive = true
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

    /// <summary>
    /// Rule A writes to a shared path; Rule C reads from that exact path - the
    /// "same path" overlap scenario from <c>DependencyGraphTests</c>, so C
    /// depends on A.
    /// </summary>
    private static (Rule ruleA, Rule ruleC) _buildDependentRulePair(
        HannibalContext context, string userId, string prefix, TimeSpan maxAge)
    {
        var alpha = new Storage { UserId = userId, Technology = "local", UriSchema = $"{prefix}-alpha-{Guid.NewGuid():N}", IsActive = true };
        var mid = new Storage { UserId = userId, Technology = "local", UriSchema = $"{prefix}-mid-{Guid.NewGuid():N}", IsActive = true };
        var gamma = new Storage { UserId = userId, Technology = "local", UriSchema = $"{prefix}-gamma-{Guid.NewGuid():N}", IsActive = true };
        context.Storages.AddRange(alpha, mid, gamma);

        var alphaEp = new Endpoint { Name = $"{prefix}-alpha-ep-{Guid.NewGuid():N}", UserId = userId, Storage = alpha, Path = "/data", IsActive = true };
        var midEpAsDestOfA = new Endpoint { Name = $"{prefix}-mid-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = mid, Path = "/data", IsActive = true };
        var midEpAsSourceOfC = new Endpoint { Name = $"{prefix}-mid-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = mid, Path = "/data", IsActive = true };
        var gammaEp = new Endpoint { Name = $"{prefix}-gamma-ep-{Guid.NewGuid():N}", UserId = userId, Storage = gamma, Path = "/data", IsActive = true };
        context.Endpoints.AddRange(alphaEp, midEpAsDestOfA, midEpAsSourceOfC, gammaEp);

        var ruleA = new Rule
        {
            Name = $"{prefix}-ruleA-{Guid.NewGuid():N}", Comment = "", UserId = userId,
            SourceEndpoint = alphaEp, DestinationEndpoint = midEpAsDestOfA,
            Operation = Rule.RuleOperation.Copy, MaxDestinationAge = maxAge
        };
        var ruleC = new Rule
        {
            Name = $"{prefix}-ruleC-{Guid.NewGuid():N}", Comment = "", UserId = userId,
            SourceEndpoint = midEpAsSourceOfC, DestinationEndpoint = gammaEp,
            Operation = Rule.RuleOperation.Copy, MaxDestinationAge = maxAge
        };

        return (ruleA, ruleC);
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
