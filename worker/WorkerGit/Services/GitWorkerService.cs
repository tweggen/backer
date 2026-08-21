using Hannibal;
using Hannibal.Client;
using Hannibal.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tools;
using WorkerGit.Configuration;

namespace WorkerGit.Services;

/**
 * Outcome of the startup <c>git --version</c> probe (plan-git-repo-storage.md
 * §6 "Minimum git 2.31"). A separate value type (rather than a bool) so a
 * failure carries a human-readable reason for the one warning
 * <see cref="GitWorkerService"/> logs and so tests can assert on the reason,
 * not just the pass/fail bit.
 */
public sealed record GitVersionProbeResult(bool IsAvailable, Version? Version, string? Reason)
{
    public static GitVersionProbeResult Available(Version version) => new(true, version, null);

    public static GitVersionProbeResult Unavailable(string reason) => new(false, null, reason);

    /** The value before <see cref="GitWorkerService.ExecuteAsync"/> has probed anything. */
    public static readonly GitVersionProbeResult NotYetProbed = new(false, null, "not probed yet");
}

/**
 * Agent-side hosted service for the git mirror engine
 * (plan-git-repo-storage.md Design §2), sibling to
 * <c>WorkerRClone.Services.RCloneService</c> but deliberately much simpler:
 * no state machine, because there is no external process lifecycle to
 * manage (rclone's RC server vs. one short-lived git invocation per step) -
 * just a startup probe, an acquisition loop, and per-job execution.
 */
public sealed class GitWorkerService : BackgroundService
{
    /** Plan §6: the floor is 2.31, for GIT_CONFIG_COUNT/GIT_CONFIG_KEY_n support. */
    private static readonly Version _minimumGitVersion = new(2, 31);

    /**
     * Plan §6 "Heartbeat" / Gate D AC8: comfortably under both the ≤30s
     * requirement and the server's 120s timeout
     * (HannibalServiceJobs.cs:205-222).
     */
    private static readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(25);

    /** Safety-net poll, mirroring RCloneService._jobPollInterval (RCloneService.cs:67). */
    private static readonly TimeSpan _jobPollInterval = TimeSpan.FromSeconds(120);

    /**
     * Small deliberate cap: unlike an rclone transfer (many small parallel
     * file operations), one git mirror job is a handful of large sequential
     * network calls (fetch, then push) sharing one per-source cache lock
     * (plan §7) - a large concurrency budget would mostly just contend on
     * that lock rather than do useful extra work.
     */
    private const int _maxConcurrentJobs = 2;

    private static readonly object _classLock = new();
    private static int _nextId;

    private readonly object _lo = new();
    private readonly Dictionary<int, _RunningGitJob> _runningJobs = new();

    private readonly string _ownerId;
    private readonly ILogger<GitWorkerService> _logger;
    private readonly GitWorkerOptions _options;
    private readonly GitCliRunner _cliRunner;
    private readonly GitMirrorEngine _engine;
    private readonly HubConnection _hannibalConnection;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly INetworkIdentifier _networkIdentifier;

    private bool _isConnectionSubscribed;
    private DateTime _lastJobPollUtc = DateTime.MinValue;

    // internal so a test can assert the probe outcome without a live git binary.
    internal GitVersionProbeResult _probeResult = GitVersionProbeResult.NotYetProbed;

    // How often SkipJobAcquisition declined a fetch - mirrors
    // RCloneService._skippedJobFetchCount, internal so a test can await
    // "the fetch path fired and was refused" instead of sleeping.
    internal int _skippedJobFetchCount = 0;

    /** (jobId, phase/progress line) - fired from the engine's fetch/push stderr callback (plan §6, Gate D AC15). */
    public Action<int, string>? OnJobProgress { get; set; }

    public GitWorkerService(
        ILogger<GitWorkerService> logger,
        GitWorkerOptions options,
        GitCliRunner cliRunner,
        GitMirrorEngine engine,
        Dictionary<string, HubConnection> connections,
        IServiceScopeFactory serviceScopeFactory,
        INetworkIdentifier networkIdentifier)
    {
        lock (_classLock)
        {
            _ownerId = $"worker-git-{_nextId++}";
        }

        _logger = logger;
        _options = options;
        _cliRunner = cliRunner;
        _engine = engine;
        _hannibalConnection = connections["hannibal"];
        _serviceScopeFactory = serviceScopeFactory;
        _networkIdentifier = networkIdentifier;

        _logger.LogInformation("GitWorkerService: starting {OwnerId}.", _ownerId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _probeResult = await _probeGitAsync(stoppingToken);
        if (!_probeResult.IsAvailable)
        {
            /*
             * Exactly one clear warning (Gate D AC10), then permanent idle:
             * no hub subscription, no acquire call, ever, for the lifetime of
             * this host. Returning from ExecuteAsync here is a normal,
             * successful completion of a BackgroundService - it does not
             * bring the rest of the agent down.
             */
            _logger.LogWarning(
                "GitWorkerService: git is not available ({Reason}); this agent will never advertise the "
                + "\"git\" capability or acquire git jobs.", _probeResult.Reason);
            return;
        }

        _logger.LogInformation("GitWorkerService: git {Version} found, ready to acquire git jobs.", _probeResult.Version);

        _subscribeToHub();

        await _tryAcquireJobAsync(stoppingToken);
        _lastJobPollUtc = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTime.UtcNow - _lastJobPollUtc >= _jobPollInterval)
            {
                _lastJobPollUtc = DateTime.UtcNow;
                await _tryAcquireJobAsync(stoppingToken);
            }

            try
            {
                await Task.Delay(5_000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /**
     * <c>git --version</c> through <see cref="GitCliRunner"/>, so the probe
     * exercises the exact same process-start path a real job would - a
     * missing/unreadable GitPath surfaces here the same way it would during
     * a job.
     */
    private async Task<GitVersionProbeResult> _probeGitAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.CacheRoot))
            {
                return GitVersionProbeResult.Unavailable("GitWorkerOptions.CacheRoot is not set.");
            }

            Directory.CreateDirectory(_options.CacheRoot);

            var result = await _cliRunner.RunAsync(
                new[] { "--version" }, _options.CacheRoot, null, null, cancellationToken);

            if (!result.Success)
            {
                return GitVersionProbeResult.Unavailable(
                    $"'{_options.GitPath} --version' did not complete successfully "
                    + $"({result.Outcome}, exit {result.ExitCode}): {_firstNonEmptyLine(result.StdErr, result.Message)}");
            }

            var version = _parseGitVersion(result.StdOut);
            if (version is null)
            {
                return GitVersionProbeResult.Unavailable(
                    $"could not parse a version number out of: {result.StdOut.Trim()}");
            }

            if (version < _minimumGitVersion)
            {
                return GitVersionProbeResult.Unavailable(
                    $"git {version} is older than the required minimum {_minimumGitVersion} "
                    + "(GIT_CONFIG_COUNT/GIT_CONFIG_KEY_n support, plan §6).");
            }

            return GitVersionProbeResult.Available(version);
        }
        catch (Exception e)
        {
            // Setup failures (missing binary, cannot start the process) throw
            // out of GitCliRunner.RunAsync by contract - this is the one
            // place that turns "git is simply not there" into idle, not a
            // crash of the whole agent.
            return GitVersionProbeResult.Unavailable($"could not run '{_options.GitPath} --version': {e.Message}");
        }
    }

    private static Version? _parseGitVersion(string stdout)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            stdout, @"version\s+(?<major>\d+)\.(?<minor>\d+)(\.(?<patch>\d+))?");
        if (!match.Success)
        {
            return null;
        }

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        return new Version(major, minor, patch);
    }

    private void _subscribeToHub()
    {
        if (_isConnectionSubscribed)
        {
            return;
        }

        // Same connection object RCloneService subscribes to
        // (connections["hannibal"]); a second .On registration for
        // "NewJobAvailable" on one HubConnection is supported and both
        // handlers fire independently.
        _hannibalConnection.On("NewJobAvailable", async () =>
        {
            await _tryAcquireJobAsync(CancellationToken.None);
        });

        _isConnectionSubscribed = true;
    }

    private async Task _tryAcquireJobAsync(CancellationToken cancellationToken)
    {
        if (!_probeResult.IsAvailable)
        {
            return;
        }

        if (_options.SkipJobAcquisition)
        {
            /*
             * This agent observes without working; the user's other agents
             * are meant to pick the jobs up instead (mirrors
             * RCloneService._triggerFetchJobAsync's SkipJobAcquisition check).
             */
            Interlocked.Increment(ref _skippedJobFetchCount);
            _logger.LogInformation("GitWorkerService: SkipJobAcquisition is set, leaving jobs to other agents.");
            return;
        }

        lock (_lo)
        {
            if (_runningJobs.Count >= _maxConcurrentJobs)
            {
                _logger.LogDebug("GitWorkerService: at the concurrent job cap ({Cap}), not acquiring more.", _maxConcurrentJobs);
                return;
            }
        }

        try
        {
            Job? job;
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                job = await hannibalService.AcquireNextJobAsync(
                    new()
                    {
                        // AcquireParams.Username is not read by the server -
                        // HannibalServiceJobs.AcquireNextJobAsync resolves the
                        // caller from the authenticated request instead
                        // (HannibalServiceJobs.cs:125, the field is only
                        // logged in a commented-out line). RCloneService's
                        // hardcoded email here (RCloneService.cs:884) is a
                        // known defect, not a contract - leave it empty.
                        Username = "",
                        Capabilities = "git",
                        Owner = _ownerId,
                        Networks = _networkIdentifier?.GetCurrentNetwork() ?? "Unknown"
                    },
                    cancellationToken);
            }

            if (job is null)
            {
                // The client turns both "no job" and a non-success response
                // into null (HannibalServiceClientJobs.cs:41-50) - this is
                // the normal idle case, same as RCloneService.
                return;
            }

            _startJob(job);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "GitWorkerService: exception while acquiring a job.");
        }
    }

    private void _startJob(Job job)
    {
        var cts = new CancellationTokenSource();
        lock (_lo)
        {
            _runningJobs[job.Id] = new _RunningGitJob(job, cts);
        }

        _logger.LogInformation("GitWorkerService: acquired job {JobId} ({Operation}).", job.Id, job.Operation);

        // Deliberately not awaited here and not tied to the host's
        // stoppingToken: cancellation for one job is driven by its own cts
        // (abort, or StopAsync below), not by the acquisition loop's token.
        _ = Task.Run(() => _runJobAsync(job, cts));
    }

    private async Task _runJobAsync(Job job, CancellationTokenSource cts)
    {
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeatTask = _runHeartbeatLoopAsync(job.Id, cts.Token, heartbeatStop.Token);

        try
        {
            if (job.Operation == Rule.RuleOperation.Sync)
            {
                // Gate E ships §5's guards, which is what makes Sync safe to
                // run at all; the engine is never called for it here, so
                // there is no risk of it forcing/deleting anything.
                const string message =
                    "Sync for git ships with the safety guards in a later gate (Gate E, plan §5: "
                    + "zero-ref/shrink/force-push/adopt guards) - refusing to run it now.";
                _logger.LogWarning("GitWorkerService: job {JobId} requested Sync: {Message}", job.Id, message);
                await _reportAsync(job.Id, Job.JobState.DoneFailure, cts.Token);
                return;
            }

            var request = _buildMirrorRequest(job);
            var result = await _engine.ExecuteAsync(request, cts.Token);

            var state = result.Outcome switch
            {
                GitMirrorOutcome.Success => Job.JobState.DoneSuccess,
                GitMirrorOutcome.DoneWithErrors => Job.JobState.DoneWithErrors,
                _ => Job.JobState.DoneFailure
            };

            _logger.LogInformation(
                "GitWorkerService: job {JobId} finished as {State}: {Message}", job.Id, state, result.Message);
            await _reportAsync(job.Id, state, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // TryAbortJobAsync already reported Cancelled; StopAsync already
            // reports DoneFailure for everything still running. Either way
            // the report has already gone out - nothing further to do here.
            _logger.LogInformation("GitWorkerService: job {JobId} was cancelled.", job.Id);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "GitWorkerService: job {JobId} threw while executing.", job.Id);
            await _reportAsync(job.Id, Job.JobState.DoneFailure, CancellationToken.None);
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeatTask; } catch { /* best effort */ }

            lock (_lo)
            {
                _runningJobs.Remove(job.Id);
            }

            cts.Dispose();
        }
    }

    /**
     * Reports Executing on a fixed cadence for as long as the job's engine
     * call runs (Gate D AC8's mechanism). Stops as soon as either the job's
     * own token fires (abort/shutdown) or <paramref name="stopToken"/> does
     * (normal completion, signalled by the caller's finally block).
     */
    private async Task _runHeartbeatLoopAsync(int jobId, CancellationToken jobToken, CancellationToken stopToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobToken, stopToken);
        try
        {
            while (true)
            {
                await Task.Delay(_heartbeatInterval, linked.Token);
                await _reportAsync(jobId, Job.JobState.Executing, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the job finished, was aborted, or the host is stopping.
        }
    }

    private GitMirrorRequest _buildMirrorRequest(Job job)
    {
        var sourceStorage = job.SourceEndpoint.Storage;
        var destinationStorage = job.DestinationEndpoint.Storage;

        var sourceUrl = GitRemoteUrl.Normalize(sourceStorage.Host, job.SourceEndpoint.Path);
        var destinationUrl = GitRemoteUrl.Normalize(destinationStorage.Host, job.DestinationEndpoint.Path);

        var operation = job.Operation switch
        {
            Rule.RuleOperation.Nop => GitMirrorOperation.Nop,
            Rule.RuleOperation.Copy => GitMirrorOperation.Copy,
            // Sync is intercepted in _runJobAsync before this is ever
            // called; anything else reaching here is itself a bug, and Nop
            // (never touch a remote) is the safe direction to fail in.
            _ => GitMirrorOperation.Nop
        };

        return new GitMirrorRequest
        {
            Source = new GitMirrorEndpoint
            {
                RemoteUrl = sourceUrl,
                Username = sourceStorage.Username ?? string.Empty,
                Token = sourceStorage.Password ?? string.Empty
            },
            Destination = new GitMirrorEndpoint
            {
                RemoteUrl = destinationUrl,
                Username = destinationStorage.Username ?? string.Empty,
                Token = destinationStorage.Password ?? string.Empty
            },
            Operation = operation,
            UserId = sourceStorage.UserId,
            SourceUriSchema = sourceStorage.UriSchema,
            OnProgress = line => OnJobProgress?.Invoke(job.Id, line)
        };
    }

    private async Task _reportAsync(int jobId, Job.JobState state, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
            await hannibalService.ReportJobAsync(
                new() { JobId = jobId, State = state, Owner = _ownerId }, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "GitWorkerService: failed to report job {JobId} as {State}.", jobId, state);
        }
    }

    /**
     * Aborts a job this service owns (Gate D AC13's mechanism). Returns
     * false, with no side effect at all, when the job is not (or no longer)
     * one of this service's - the caller (BackerControlHub, Program.cs)
     * relies on that to safely try both engines for a job id it does not
     * itself know the engine of.
     */
    public async Task<bool> TryAbortJobAsync(int jobId)
    {
        CancellationTokenSource cts;
        lock (_lo)
        {
            if (!_runningJobs.TryGetValue(jobId, out var running))
            {
                return false;
            }

            cts = running.Cts;
        }

        _logger.LogInformation("GitWorkerService: aborting job {JobId}.", jobId);

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished and disposed between the lookup above and here - fine.
        }

        await _reportAsync(jobId, Job.JobState.Cancelled, CancellationToken.None);
        return true;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("GitWorkerService: StopAsync called.");

        List<KeyValuePair<int, CancellationTokenSource>> jobsToStop;
        lock (_lo)
        {
            jobsToStop = _runningJobs.Select(kvp => new KeyValuePair<int, CancellationTokenSource>(kvp.Key, kvp.Value.Cts)).ToList();
        }

        foreach (var (_, cts) in jobsToStop)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (jobsToStop.Count > 0)
        {
            _logger.LogInformation(
                "GitWorkerService: reporting {Count} in-flight git job(s) as failed due to shutdown.", jobsToStop.Count);

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                foreach (var (jobId, _) in jobsToStop)
                {
                    try
                    {
                        await hannibalService.ReportJobAsync(
                            new() { JobId = jobId, State = Job.JobState.DoneFailure, Owner = _ownerId },
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "GitWorkerService: failed to report job {JobId} during shutdown.", jobId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GitWorkerService: failed to create scope for reporting jobs during shutdown.");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private static string _firstNonEmptyLine(string text, string? fallback)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return string.IsNullOrEmpty(line) ? (fallback ?? "(no output)") : line;
    }

    private sealed record _RunningGitJob(Job Job, CancellationTokenSource Cts);
}
