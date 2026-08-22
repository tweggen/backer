using System.Diagnostics;
using FluentAssertions;
using WorkerGit.Configuration;
using WorkerGit.Services;
using WorkerGit.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace WorkerGit.Tests;

/// <summary>
/// Opt-in, live smoke test for the git mirror engine (plan-git-repo-storage.md
/// Gate H): it drives <see cref="GitMirrorEngine"/> exactly the way the agent
/// does, against two real remotes over the real network - no stubbed git
/// server, no bare repos in a temp directory.
///
/// <para><b>Skipped by default.</b> See <see cref="LiveGitFactAttribute"/> for
/// the full environment contract.</para>
///
/// <para>What it proves (Gate H AC2): a full <see cref="GitMirrorOperation.Copy"/>
/// mirror from a real source to a real, pre-existing destination completes
/// with no ref errors; <c>ls-remote</c> over
/// <see cref="GitMirrorEngine.MirroredNamespaces"/> agrees on both sides by
/// name and SHA; and the recorded argv never used a refspec outside that
/// namespace set - the entire point of plan §3's ref-namespace policy,
/// verified against a source that is expected to have at least one open pull
/// request so GitHub's <c>refs/pull/*</c> hidden refs actually exist to (not)
/// leak. A second run against the now-mirrored destination transfers nothing.</para>
///
/// <para>What it never does: touch any repository other than the two named by
/// the environment, send a refspec outside MIRRORED, or run a single git
/// command when the gate is off (Gate H AC1).</para>
/// </summary>
public sealed class LiveGitMirrorSmokeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _cacheRoot =
        Directory.CreateTempSubdirectory("WorkerGit.Tests.Live.").FullName;

    public LiveGitMirrorSmokeTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheRoot))
            {
                Directory.Delete(_cacheRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort - a lingering handle on Windows must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LiveGitFact]
    public async Task LiveMirror_SourceToDestination_CompletesCleanlyAndSecondRunTransfersNothing()
    {
        var options = _buildOptions();
        var request = _buildRequest();

        _output.WriteLine($"Source:      {request.Source.RemoteUrl}");
        _output.WriteLine($"Destination: {request.Destination.RemoteUrl}");
        _output.WriteLine($"AllowAdopt:  {request.AllowAdopt}");

        // Confirms AC2's premise: the source must actually carry refs/pull/*
        // for this run to exercise anything. Not a hard failure - a
        // misconfigured fixture repo should not sink an otherwise-green run -
        // but loudly warned about, since a silent pass here would prove nothing.
        var sourcePullRefs = await _lsRemoteRawAsync(options, request.Source, "refs/pull/*");
        _output.WriteLine(
            $"Source refs/pull/* count: {sourcePullRefs.Count} "
            + "(Gate H AC2 wants >=1 open PR on the source so this namespace is populated)");
        if (sourcePullRefs.Count == 0)
        {
            _output.WriteLine(
                "WARNING: source has no refs/pull/* refs. This run does not actually exercise "
                + "GitHub's hidden-ref protection - use a source repo with an open PR.");
        }

        // --- First run: full mirror ---
        var trace1 = new RecordingGitCommandTrace();
        var engine1 = _buildEngine(options, trace1);

        var stopwatch1 = Stopwatch.StartNew();
        var result1 = await engine1.ExecuteAsync(request, CancellationToken.None);
        stopwatch1.Stop();

        _output.WriteLine(
            $"First run: outcome={result1.Outcome} transferPerformed={result1.TransferPerformed} "
            + $"duration={stopwatch1.Elapsed}");
        _output.WriteLine($"First run message: {result1.Message}");

        result1.RefErrors.Should().BeEmpty(
            "Gate H AC2 requires zero ref errors, but got: "
            + string.Join("; ", result1.RefErrors.Select(e => $"{e.RefName} ({e.Reason})")));

        if (result1.Outcome == GitMirrorOutcome.DoneWithErrors)
        {
            // With RefErrors already asserted empty above, the only remaining
            // way to reach DoneWithErrors is the LFS-only branch of
            // GitMirrorEngine._buildResult - acceptable per this gate's brief,
            // anything else is not.
            result1.Message.Should().ContainEquivalentOf(
                "LFS",
                $"the only acceptable DoneWithErrors reason for a clean mirror is an LFS-only finding, "
                + $"but got: {result1.Message}");
        }
        else
        {
            result1.Outcome.Should().Be(GitMirrorOutcome.Success);
        }

        var allArguments1 = trace1.Records.SelectMany(r => r.Arguments).ToArray();
        allArguments1.Should().NotContain(
            a => a.Contains("refs/pull", StringComparison.Ordinal),
            "the engine must never pass a refs/pull/* refspec to git - the whole point of §3's MIRRORED policy");
        allArguments1.Should().NotContain("--mirror");
        allArguments1.Should().NotContain(a => a == "refs/*:refs/*" || a == "+refs/*:refs/*");

        // --- ls-remote both sides over MIRRORED, independently of the engine's internal probe ---
        var sourceRefs = await _lsRemoteParsedAsync(options, request.Source, GitMirrorEngine.MirroredNamespaces);
        var destinationRefs =
            await _lsRemoteParsedAsync(options, request.Destination, GitMirrorEngine.MirroredNamespaces);

        _output.WriteLine($"Source MIRRORED ref count:      {sourceRefs.Count}");
        _output.WriteLine($"Destination MIRRORED ref count: {destinationRefs.Count}");

        destinationRefs.Should().BeEquivalentTo(
            sourceRefs, "Gate H AC2: ls-remote over MIRRORED must match on both sides (names and SHAs) after the mirror");

        // --- Second run: already in sync, nothing to transfer ---
        var trace2 = new RecordingGitCommandTrace();
        var engine2 = _buildEngine(options, trace2);

        var stopwatch2 = Stopwatch.StartNew();
        var result2 = await engine2.ExecuteAsync(request, CancellationToken.None);
        stopwatch2.Stop();

        _output.WriteLine(
            $"Second run: outcome={result2.Outcome} transferPerformed={result2.TransferPerformed} "
            + $"duration={stopwatch2.Elapsed}");
        _output.WriteLine($"Second run message: {result2.Message}");

        result2.Outcome.Should().Be(GitMirrorOutcome.Success);
        result2.TransferPerformed.Should().BeFalse(
            "Gate H AC2: a second run against an already-mirrored destination must transfer nothing");
        result2.Message.Should().Contain("already in sync");

        trace2.Records.Should().HaveCount(2, "the already-in-sync fast path is exactly two ls-remote calls");
        trace2.Records.Should().OnlyContain(
            r => r.Arguments.Count > 0 && r.Arguments[0] == "ls-remote",
            "the second run must not fetch or push anything");

        // --- Gate H AC3 evidence: whether this forge reserves refs/pull/* too.
        // This is observational only - the plan asks the human to record the
        // answer, since a single destination test account cannot establish
        // what every forge does in general. ---
        var destinationPullRefs = await _lsRemoteRawAsync(options, request.Destination, "refs/pull/*");
        _output.WriteLine(
            $"Destination refs/pull/* count: {destinationPullRefs.Count} "
            + "(record in Gate H AC3 whether this forge reserves refs/pull/* the way GitHub does)");

        // --- Gate H AC4: observations to transcribe into the plan document ---
        _output.WriteLine("=== Gate H AC4 observations (record these in docs/plan-git-repo-storage.md) ===");
        _output.WriteLine($"Source:                        {request.Source.RemoteUrl}");
        _output.WriteLine($"Destination:                   {request.Destination.RemoteUrl}");
        _output.WriteLine($"Mirrored ref count:            {sourceRefs.Count}");
        _output.WriteLine($"First run duration (mirror):   {stopwatch1.Elapsed}");
        _output.WriteLine($"Second run duration (no-op):   {stopwatch2.Elapsed}");
    }

    /// <summary>
    /// Guards the gate itself (mirrors <c>LiveOneDriveCredentialCheckTests</c>):
    /// with <see cref="LiveGitFactAttribute.EnableVariable"/> unset, the live
    /// smoke test must be skipped, and the reason must say how to enable it.
    /// This is the AC1 proof: it always runs (it is a plain <see cref="FactAttribute"/>),
    /// so the suite catches it if the gate attribute ever stops skipping cleanly.
    /// </summary>
    [Fact]
    public void LiveSmokeTest_IsSkipped_UnlessExplicitlyEnabled()
    {
        var enabled = Environment.GetEnvironmentVariable(LiveGitFactAttribute.EnableVariable) == "1";
        var skipReason = LiveGitFactAttribute.ComputeSkipReason();

        if (enabled)
        {
            _output.WriteLine($"{LiveGitFactAttribute.EnableVariable}=1 - live smoke test opted in.");
            return;
        }

        skipReason.Should().NotBeNull();
        skipReason.Should().Contain(LiveGitFactAttribute.EnableVariable);
        skipReason.Should().Contain(LiveGitFactAttribute.SourceUrlVariable);
        skipReason.Should().Contain(LiveGitFactAttribute.DestinationUrlVariable);
        _output.WriteLine($"Live smoke test skip reason: {skipReason}");
    }

    private GitWorkerOptions _buildOptions() => new()
    {
        GitPath = "git",
        CacheRoot = _cacheRoot,
        // A first mirror of a real, possibly large repository needs far more
        // headroom than the offline tests' bare-repo fixtures.
        JobTimeout = TimeSpan.FromMinutes(30),
        StallTimeout = TimeSpan.FromMinutes(5)
    };

    private GitMirrorEngine _buildEngine(GitWorkerOptions options, RecordingGitCommandTrace trace)
    {
        var runner = new GitCliRunner(options, trace);
        var cacheManager = new GitCacheManager(options, runner);
        return new GitMirrorEngine(options, runner, cacheManager);
    }

    private GitMirrorRequest _buildRequest() => new()
    {
        Source = new GitMirrorEndpoint
        {
            RemoteUrl = LiveGitFactAttribute.SourceUrl!,
            Username = LiveGitFactAttribute.SourceUsername,
            Token = LiveGitFactAttribute.SourceToken
        },
        Destination = new GitMirrorEndpoint
        {
            RemoteUrl = LiveGitFactAttribute.DestinationUrl!,
            Username = LiveGitFactAttribute.DestinationUsername,
            Token = LiveGitFactAttribute.DestinationToken
        },
        Operation = GitMirrorOperation.Copy,
        UserId = "live-smoke-test",
        SourceUriSchema = "live-git",
        AllowAdopt = LiveGitFactAttribute.AllowAdopt,
        // Forwarded to test output so a long first mirror of a large
        // repository is observably alive rather than looking hung.
        OnProgress = line => _output.WriteLine($"[git] {line}")
    };

    private async Task<IReadOnlyList<string>> _lsRemoteRawAsync(
        GitWorkerOptions options, GitMirrorEndpoint endpoint, string pattern)
    {
        var runner = new GitCliRunner(options);
        var credentials = new GitCredentials { Username = endpoint.Username, Token = endpoint.Token };
        var result = await runner.RunAsync(
            new[] { "ls-remote", endpoint.RemoteUrl, pattern }, options.CacheRoot!, credentials, null,
            CancellationToken.None);

        result.Success.Should().BeTrue(
            $"ls-remote against '{endpoint.RemoteUrl}' pattern '{pattern}' must succeed: {result.StdErr}");

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private async Task<Dictionary<string, string>> _lsRemoteParsedAsync(
        GitWorkerOptions options, GitMirrorEndpoint endpoint, IReadOnlyList<string> patterns)
    {
        var runner = new GitCliRunner(options);
        var credentials = new GitCredentials { Username = endpoint.Username, Token = endpoint.Token };
        var args = new List<string> { "ls-remote", endpoint.RemoteUrl };
        args.AddRange(patterns);

        var result = await runner.RunAsync(args, options.CacheRoot!, credentials, null, CancellationToken.None);
        result.Success.Should().BeTrue($"ls-remote against '{endpoint.RemoteUrl}' must succeed: {result.StdErr}");

        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in result.StdOut.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0)
            {
                continue;
            }

            refs[line[(tabIndex + 1)..]] = line[..tabIndex];
        }

        return refs;
    }
}
