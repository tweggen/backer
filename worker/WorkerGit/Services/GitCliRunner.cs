using System.Diagnostics;
using System.IO;
using System.Text;
using WorkerGit.Configuration;

namespace WorkerGit.Services;

/**
 * Every git invocation goes through here (plan-git-repo-storage.md §6). One
 * instance is shared across a job; each <see cref="RunAsync"/> call is
 * independent and gets its own throwaway run directory (isolated HOME and
 * askpass helper), so concurrent invocations never share mutable state.
 */
public sealed class GitCliRunner
{
    private static readonly TimeSpan _watchdogPollInterval = TimeSpan.FromMilliseconds(150);

    private readonly GitWorkerOptions _options;
    private readonly IGitCommandTrace _trace;

    public GitCliRunner(GitWorkerOptions options, IGitCommandTrace? trace = null)
    {
        _options = options;
        _trace = trace ?? NullGitCommandTrace.Instance;
    }

    /**
     * Runs one git invocation to completion, killed on timeout/stall/
     * cancellation. Never throws for a failing git process - a non-zero exit
     * code is a normal <see cref="GitRunResult"/>, not an exception; only
     * setup failures (cannot create the run directory, cannot start the
     * process) throw.
     */
    public async Task<GitRunResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        GitCredentials? credentials = null,
        Action<string>? onProgressLine = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        if (string.IsNullOrEmpty(_options.CacheRoot))
        {
            throw new InvalidOperationException(
                "GitWorkerOptions.CacheRoot must be set before running git - "
                + "the isolated HOME directory and askpass helper live under it.");
        }

        var runDirectory = Path.Combine(_options.CacheRoot, ".runs", Guid.NewGuid().ToString("N"));
        var homeDirectory = Path.Combine(runDirectory, "home");
        Directory.CreateDirectory(homeDirectory);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var askpassPath = _writeAskpassScript(runDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = _options.GitPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _applyEnvironment(startInfo, homeDirectory, askpassPath, credentials);
            if (extraEnvironment is not null)
            {
                // Applied last, and only used by the adopt marker's
                // deterministic commit-tree call (GIT_AUTHOR_*/GIT_COMMITTER_*)
                // - none of those keys overlap the security-critical ones set
                // above, so there is nothing for a caller to accidentally
                // clobber (GIT_TERMINAL_PROMPT, GIT_ASKPASS, GIT_CONFIG_*, HOME).
                foreach (var (key, value) in extraEnvironment)
                {
                    startInfo.EnvironmentVariables[key] = value;
                }
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var outputLock = new object();
            long lastOutputTicks = DateTime.UtcNow.Ticks;

            var process = CrossPlatformProcessManager.StartManagedProcess(startInfo);
            try
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                    lock (outputLock) { stdout.AppendLine(e.Data); }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                    lock (outputLock) { stderr.AppendLine(e.Data); }
                    onProgressLine?.Invoke(e.Data);
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try { process.StandardInput.Close(); } catch (IOException) { /* already gone */ }

                var (outcome, message) = await _waitWithWatchdogAsync(
                    process, () => Interlocked.Read(ref lastOutputTicks), stopwatch, cancellationToken);

                int exitCode;
                if (outcome == GitRunOutcome.Completed)
                {
                    exitCode = process.ExitCode;
                }
                else
                {
                    // Watchdog/cancellation fired - the process is being killed
                    // (or already was); no meaningful exit code exists.
                    exitCode = -1;
                }

                string stdoutText, stderrText;
                lock (outputLock)
                {
                    stdoutText = stdout.ToString();
                    stderrText = stderr.ToString();
                }

                _trace.Record(new GitCommandRecord(
                    arguments.ToArray(), workingDirectory,
                    outcome == GitRunOutcome.Completed ? exitCode : null,
                    stopwatch.Elapsed, outcome, message));

                return new GitRunResult
                {
                    Outcome = outcome,
                    ExitCode = exitCode,
                    StdOut = stdoutText,
                    StdErr = stderrText,
                    Message = message
                };
            }
            finally
            {
                process.Dispose();
            }
        }
        finally
        {
            _tryDeleteDirectory(runDirectory);
        }
    }

    /**
     * Races the process's own exit against the two watchdog timers and
     * external cancellation. On anything but a clean exit, kills the whole
     * process tree explicitly - <c>CrossPlatformProcessManager</c>'s
     * kill-on-host-death job object is a backstop, not a substitute.
     */
    private async Task<(GitRunOutcome Outcome, string? Message)> _waitWithWatchdogAsync(
        Process process,
        Func<long> lastOutputTicks,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var exitTask = process.WaitForExitAsync(CancellationToken.None);

        while (true)
        {
            if (exitTask.IsCompleted)
            {
                return (GitRunOutcome.Completed, null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _killProcessTree(process);
                await _awaitExitBestEffort(exitTask);
                return (GitRunOutcome.Cancelled, "The git invocation was cancelled.");
            }

            if (stopwatch.Elapsed >= _options.JobTimeout)
            {
                _killProcessTree(process);
                await _awaitExitBestEffort(exitTask);
                return (GitRunOutcome.TimedOut,
                    $"JobTimeout ({_options.JobTimeout}) elapsed before the git invocation finished.");
            }

            var sinceLastOutput = DateTime.UtcNow - new DateTime(lastOutputTicks(), DateTimeKind.Utc);
            if (sinceLastOutput >= _options.StallTimeout)
            {
                _killProcessTree(process);
                await _awaitExitBestEffort(exitTask);
                return (GitRunOutcome.Stalled,
                    $"StallTimeout ({_options.StallTimeout}) elapsed with no output from the git invocation.");
            }

            var delay = Task.Delay(_watchdogPollInterval, CancellationToken.None);
            await Task.WhenAny(exitTask, delay);
        }
    }

    private static void _killProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the call - fine.
        }
    }

    private static async Task _awaitExitBestEffort(Task exitTask)
    {
        try
        {
            await exitTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Best effort reap after Kill(); a still-running exitTask here
            // would mean the OS never released the process, nothing more we
            // can do from here.
        }
    }

    private void _applyEnvironment(
        ProcessStartInfo startInfo, string homeDirectory, string askpassPath, GitCredentials? credentials)
    {
        var env = startInfo.EnvironmentVariables;

        // §6: a 401 must not block forever waiting for a prompt the service
        // can never answer.
        env["GIT_TERMINAL_PROMPT"] = "0";

        // §6: stop the machine's system-wide gitconfig (and any credential
        // helper it configures) from silently authenticating as whoever is
        // logged into the box instead of the Storage's own PAT.
        env["GIT_CONFIG_NOSYSTEM"] = "1";

        env["HOME"] = homeDirectory;
        if (OperatingSystem.IsWindows())
        {
            env["USERPROFILE"] = homeDirectory;
        }

        // credential.helper cleared (empty value = "forget every helper");
        // core.longpaths=true so a deep pack path under a long cache root
        // does not exceed MAX_PATH. Injected via env, not -c, because these
        // must apply to *every* git invocation including init/clone.
        env["GIT_CONFIG_COUNT"] = "2";
        env["GIT_CONFIG_KEY_0"] = "credential.helper";
        env["GIT_CONFIG_VALUE_0"] = "";
        env["GIT_CONFIG_KEY_1"] = "core.longpaths";
        env["GIT_CONFIG_VALUE_1"] = "true";

        env["GIT_ASKPASS"] = askpassPath;
        env["BACKER_GIT_USERNAME"] = credentials?.Username ?? string.Empty;
        env["BACKER_GIT_TOKEN"] = credentials?.Token ?? string.Empty;
    }

    /**
     * Writes the askpass helper once per run directory. Credentials reach it
     * only through the environment (<c>BACKER_GIT_USERNAME</c>/
     * <c>BACKER_GIT_TOKEN</c>), never as script content or argv.
     */
    private static string _writeAskpassScript(string runDirectory)
    {
        Directory.CreateDirectory(runDirectory);

        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(runDirectory, "askpass.cmd");
            File.WriteAllText(path,
                "@echo off\r\n"
                + "echo %1 | findstr /i \"username\" >nul\r\n"
                + "if %errorlevel%==0 (\r\n"
                + "    echo %BACKER_GIT_USERNAME%\r\n"
                + ") else (\r\n"
                + "    echo %BACKER_GIT_TOKEN%\r\n"
                + ")\r\n");
            return path;
        }
        else
        {
            var path = Path.Combine(runDirectory, "askpass.sh");
            File.WriteAllText(path,
                "#!/bin/sh\n"
                + "case \"$1\" in\n"
                + "  *[Uu]sername*) echo \"$BACKER_GIT_USERNAME\" ;;\n"
                + "  *) echo \"$BACKER_GIT_TOKEN\" ;;\n"
                + "esac\n");
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return path;
        }
    }

    private static void _tryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort - a lingering handle (e.g. a slow-to-exit child on
            // Windows) must not fail the job over cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
