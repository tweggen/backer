using System.Diagnostics;

namespace BackerAgent.IntegrationTests.TestSupport;

/**
 * Tiny synchronous git helpers for building real bare-repository fixtures in
 * this project's own tests - deliberately independent of
 * <c>worker/WorkerGit</c>'s own <c>GitCliRunner</c> (the code under test),
 * mirroring <c>tests/WorkerGit.Tests/TestSupport/GitTestRepo.cs</c>'s
 * reasoning: a bug in the engine's runner must not also make the fixtures
 * that exercise it wrong.
 */
public static class GitFixtureRepo
{
    public static void InitBare(string bareRepoPath)
    {
        Directory.CreateDirectory(bareRepoPath);
        Run(Path.GetDirectoryName(bareRepoPath)!, "init", "--quiet", "--bare", bareRepoPath);
    }

    public static string CloneToWorkdir(string bareRepoPath, string workdirPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(workdirPath)!);
        Run(Path.GetDirectoryName(workdirPath)!, "clone", "--quiet", bareRepoPath, workdirPath);
        Run(workdirPath, "config", "user.name", "Backer Test");
        Run(workdirPath, "config", "user.email", "backer-test@example.invalid");
        return workdirPath;
    }

    public static void CreateOrphanBranch(string workdirPath, string branchName) =>
        Run(workdirPath, "checkout", "--quiet", "--orphan", branchName);

    public static string Commit(string workdirPath, string fileName, string content, string message)
    {
        File.WriteAllText(Path.Combine(workdirPath, fileName), content);
        Run(workdirPath, "add", "--", fileName);
        Run(workdirPath, "commit", "--quiet", "-m", message);
        return Run(workdirPath, "rev-parse", "HEAD").Trim();
    }

    public static void Push(string workdirPath, params string[] refspecs)
    {
        var args = new List<string> { "push", "--quiet", "origin" };
        args.AddRange(refspecs);
        Run(workdirPath, args.ToArray());
    }

    /** <c>refname -&gt; sha</c> for every ref in a bare (or workdir) repo. */
    public static Dictionary<string, string> ForEachRef(string repoPath)
    {
        var output = Run(repoPath, "for-each-ref", "--format=%(objectname)\t%(refname)");
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length == 2)
            {
                refs[parts[1]] = parts[0];
            }
        }

        return refs;
    }

    public static string Run(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git for test fixture setup.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} (in '{workingDirectory}') exited {process.ExitCode}:\n{stderr}");
        }

        return stdout;
    }
}
