namespace Hannibal.Models;

/**
 * Which transfer engine a (source, destination) endpoint pair needs
 * (plan-git-repo-storage.md Design §2). Rclone and the future git worker
 * share nothing; a pair that needs one engine on each side, or an engine
 * that does not exist yet, cannot become a runnable job at all.
 */
public enum JobEngine
{
    Rclone,
    Git,
    Unsupported
}

/**
 * Pure classification of a (source, destination) technology pair into the
 * engine that would have to run the job. No I/O - callers load whatever
 * Storage/Endpoint rows they need before calling this.
 */
public static class JobEngineClassifier
{
    private const string GitTechnology = "git";

    /**
     * Both sides rclone-capable (Technologies.IsRCloneTechnology) -> Rclone.
     * Both sides exactly "git" -> Git. Anything else - one git/one not,
     * an unknown technology on either side, technology missing entirely -
     * -> Unsupported, because no engine exists that could run it.
     */
    public static JobEngine Classify(string? sourceTechnology, string? destinationTechnology)
    {
        if (Technologies.IsRCloneTechnology(sourceTechnology) && Technologies.IsRCloneTechnology(destinationTechnology))
        {
            return JobEngine.Rclone;
        }

        if (sourceTechnology == GitTechnology && destinationTechnology == GitTechnology)
        {
            return JobEngine.Git;
        }

        return JobEngine.Unsupported;
    }

    /**
     * Convenience overload for callers that already hold both endpoints with
     * their Storage loaded - the technology lives on Storage, not Endpoint.
     */
    public static JobEngine Classify(Endpoint source, Endpoint destination)
    {
        return Classify(source.Storage?.Technology, destination.Storage?.Technology);
    }
}

/**
 * Canonical remote identity for a git endpoint (plan-git-repo-storage.md
 * Design §1 "Remote URL = Host + Path + (.git if not already present)").
 * Used both by Gate B's self-mirror rejection at rule creation and, per the
 * plan, reused at run time by later gates' self-mirror guard - so this is
 * the one place the join/suffix/case rules live.
 *
 * Two endpoints normalising to an ordinal-equal string are the same
 * repository, including when they belong to two different Storage rows
 * that merely point at the same host and path.
 */
public static class GitRemoteUrl
{
    /**
     * Join Host and Path with exactly one "/", appending ".git" unless the
     * last path segment already ends in it (case-insensitive). For an
     * http(s) Host, the scheme+host portion is lowercased (hosts are
     * case-insensitive; forge repo names generally are not, so Path is left
     * untouched) - comparison of the result is a plain ordinal string
     * comparison, never case-insensitive, so that path-casing differences
     * still count as different repositories. For a filesystem-root Host,
     * the join is resolved with Path.GetFullPath and backslashes are turned
     * into forward slashes, so two spellings of one local path normalise
     * equal regardless of separator or ".." segments.
     */
    public static string Normalize(string? host, string? path)
    {
        host = (host ?? string.Empty).Trim();
        path = (path ?? string.Empty).Trim();

        bool isHttpUrl = Uri.TryCreate(host, UriKind.Absolute, out var hostUri)
                          && (hostUri.Scheme == Uri.UriSchemeHttp || hostUri.Scheme == Uri.UriSchemeHttps);

        if (isHttpUrl)
        {
            var normalizedHost = host.TrimEnd('/').ToLowerInvariant();
            var trimmedPath = path.TrimStart('/');
            return _appendGitSuffixIfMissing($"{normalizedHost}/{trimmedPath}");
        }

        // Filesystem root: resolve through Path.GetFullPath so "D:/x/" and
        // "D:\\x" (and a Path containing "..") land on the same string, then
        // pick one separator style for a stable comparison.
        var fullPath = Path.GetFullPath(Path.Combine(host, path));
        var normalizedFsPath = fullPath.Replace('\\', '/');
        return _appendGitSuffixIfMissing(normalizedFsPath);
    }

    private static string _appendGitSuffixIfMissing(string url)
    {
        var lastSlash = url.LastIndexOf('/');
        var lastSegment = lastSlash >= 0 ? url[(lastSlash + 1)..] : url;
        return lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + ".git";
    }
}
