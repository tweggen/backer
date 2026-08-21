using System.Diagnostics;
using System.Text;

namespace WorkerGit.Tests.TestSupport;

/**
 * Tiny synchronous git helpers for building test fixtures - deliberately
 * separate from <c>WorkerGit.Services.GitCliRunner</c> (the class under
 * test) so a bug in the runner cannot silently make the fixtures wrong too.
 */
public static class GitTestRepo
{
    public static void InitBare(string bareRepoPath)
    {
        Directory.CreateDirectory(bareRepoPath);
        Run(Path.GetDirectoryName(bareRepoPath)!, "init", "--quiet", "--bare", bareRepoPath);
    }

    /** Clones <paramref name="bareRepoPath"/> into a fresh workdir and sets a local identity (never touches global/user gitconfig). */
    public static string CloneToWorkdir(string bareRepoPath, string workdirPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(workdirPath)!);
        Run(Path.GetDirectoryName(workdirPath)!, "clone", "--quiet", bareRepoPath, workdirPath);
        Run(workdirPath, "config", "user.name", "Backer Test");
        Run(workdirPath, "config", "user.email", "backer-test@example.invalid");
        return workdirPath;
    }

    /** Every test seeds its first branch from a freshly cloned empty repo, so there is nothing staged to clear yet. */
    public static void CreateOrphanBranch(string workdirPath, string branchName) =>
        Run(workdirPath, "checkout", "--quiet", "--orphan", branchName);

    public static void CreateBranch(string workdirPath, string branchName, string fromRef = "HEAD")
    {
        Run(workdirPath, "checkout", "--quiet", "-b", branchName, fromRef);
    }

    public static void Checkout(string workdirPath, string branchName)
    {
        Run(workdirPath, "checkout", "--quiet", branchName);
    }

    /** Writes one small file, commits it, and returns the new commit's SHA. */
    public static string Commit(string workdirPath, string fileName, string content, string message)
    {
        File.WriteAllText(Path.Combine(workdirPath, fileName), content);
        Run(workdirPath, "add", "--", fileName);
        Run(workdirPath, "commit", "--quiet", "-m", message);
        return Run(workdirPath, "rev-parse", "HEAD").Trim();
    }

    public static void Tag(string workdirPath, string tagName) =>
        Run(workdirPath, "tag", tagName);

    public static void AnnotatedTag(string workdirPath, string tagName, string message) =>
        Run(workdirPath, "tag", "-a", tagName, "-m", message);

    public static void Note(string workdirPath, string commitSha, string message) =>
        Run(workdirPath, "notes", "add", "-m", message, commitSha);

    public static void Push(string workdirPath, params string[] refspecs)
    {
        var args = new List<string> { "push", "--quiet", "origin" };
        args.AddRange(refspecs);
        Run(workdirPath, args.ToArray());
    }

    /**
     * <c>refname -&gt; sha</c> for every ref in a bare (or workdir) repo, or -
     * with <paramref name="patterns"/> - only refs matching those
     * <c>for-each-ref</c> patterns (e.g. the MIRRORED namespaces), which lets
     * a test compare "just what the engine mirrors" without the Gate E adopt
     * marker (outside MIRRORED by design) throwing the comparison off.
     */
    public static Dictionary<string, string> ForEachRef(string repoPath, params string[] patterns)
    {
        var args = new List<string> { "for-each-ref", "--format=%(objectname)\t%(refname)" };
        args.AddRange(patterns);
        var output = Run(repoPath, args.ToArray());
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

    public static void DeleteRef(string repoPath, string refName) =>
        Run(repoPath, "update-ref", "-d", refName);

    /**
     * Raw, default-format <c>git for-each-ref</c> output (deterministically
     * sorted by refname) - for a true byte-identical before/after comparison
     * across a tripped guard (Gate E's constraint), stronger than comparing
     * two <see cref="ForEachRef"/> dictionaries for equivalence.
     */
    public static string ForEachRefRaw(string repoPath) => Run(repoPath, "for-each-ref");

    /**
     * Creates a direct (non-symbolic) ref pointing at whatever <paramref name="targetSha"/>
     * resolves to - used to seed foreign-namespace refs (e.g. a fake
     * <c>refs/pull/1/head</c>) and marker refs directly, without going
     * through the engine under test.
     */
    public static void UpdateRef(string repoPath, string refName, string targetSha) =>
        Run(repoPath, "update-ref", refName, targetSha);

    /**
     * <c>--exclude=refs/backer/*</c> before <c>--all</c> (git resolves
     * <c>--exclude</c> patterns against whatever ref-selecting option follows,
     * per gitrevisions(7)) so the Gate E adopt marker - a real, reachable
     * commit outside MIRRORED - never shows up as a spurious extra commit in
     * a full-history comparison between source and destination.
     */
    public static IReadOnlyList<string> LogAllHashes(string repoOrWorkdirPath) =>
        Run(repoOrWorkdirPath, "log", "--exclude=refs/backer/*", "--all", "--format=%H")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

    public static string ReadFileAtHead(string workdirPath, string relativeFileName) =>
        File.ReadAllText(Path.Combine(workdirPath, relativeFileName));

    /**
     * Destroys a pack file inside a bare cache to simulate corruption (AC5).
     * Overwrites rather than merely truncates - truncation past the header
     * can leave the "PACK" magic bytes intact, which git tolerates far more
     * gracefully than a header it does not recognise at all.
     */
    public static void CorruptOnePackFile(string bareRepoPath)
    {
        var packDir = Path.Combine(bareRepoPath, "objects", "pack");
        var pack = Directory.EnumerateFiles(packDir, "*.pack").FirstOrDefault()
            ?? throw new InvalidOperationException($"No pack file found under '{packDir}' to corrupt.");

        // git marks pack files read-only; clear that before overwriting.
        var attributes = File.GetAttributes(pack);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(pack, attributes & ~FileAttributes.ReadOnly);
        }

        File.WriteAllBytes(pack, "CORRUPTED-NOT-A-PACKFILE"u8.ToArray());
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
