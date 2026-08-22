using Xunit;

namespace WorkerGit.Tests.TestSupport;

/// <summary>
/// A <see cref="FactAttribute"/> that opts the git-mirror live smoke test out
/// unless the full environment contract below is configured. Mirrors
/// <c>WorkerRClone.Tests.TestSupport.LiveOAuthFactAttribute</c>'s pattern:
/// xUnit v2 has no built-in dynamic skip, so this attribute evaluates the
/// environment at test <em>discovery</em> time and sets <see cref="FactAttribute.Skip"/>.
/// A skipped test never runs, so with the gate off there is provably not a
/// single network call or git invocation (plan-git-repo-storage.md Gate H AC1).
///
/// <para>
/// Enable with:
/// <code>
/// $env:BACKER_LIVE_GIT_TEST = "1"
/// $env:BACKER_LIVE_GIT_SOURCE = "https://github.com/owner/repo"
/// $env:BACKER_LIVE_GIT_SOURCE_USER = "&lt;source username - may be blank for a public source&gt;"
/// $env:BACKER_LIVE_GIT_SOURCE_TOKEN = "&lt;source token - may be blank for a public source&gt;"
/// $env:BACKER_LIVE_GIT_DEST = "https://codeberg.org/owner/repo"
/// $env:BACKER_LIVE_GIT_DEST_USER = "&lt;destination username&gt;"
/// $env:BACKER_LIVE_GIT_DEST_TOKEN = "&lt;destination token - required, this test pushes to it&gt;"
/// # Optional - only needed for a first run against a non-empty, unmarked destination:
/// $env:BACKER_LIVE_GIT_ALLOW_ADOPT = "1"
/// </code>
/// </para>
///
/// <para>
/// The source repository should carry at least one OPEN pull request -
/// Gate H AC2's entire point is proving the engine never sends a refspec
/// touching GitHub's hidden <c>refs/pull/*</c> namespace, and that namespace
/// is only populated when a PR is actually open. The destination repository
/// MUST already exist (this engine never creates a forge repository) and
/// WILL BE PUSHED TO by this test - it should be empty or already a mirror of
/// the source; see plan-git-repo-storage.md §5's adopt guard for what happens
/// otherwise (and what <c>BACKER_LIVE_GIT_ALLOW_ADOPT</c> overrides).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LiveGitFactAttribute : FactAttribute
{
    public const string EnableVariable = "BACKER_LIVE_GIT_TEST";
    public const string SourceUrlVariable = "BACKER_LIVE_GIT_SOURCE";
    public const string SourceUserVariable = "BACKER_LIVE_GIT_SOURCE_USER";
    public const string SourceTokenVariable = "BACKER_LIVE_GIT_SOURCE_TOKEN";
    public const string DestinationUrlVariable = "BACKER_LIVE_GIT_DEST";
    public const string DestinationUserVariable = "BACKER_LIVE_GIT_DEST_USER";
    public const string DestinationTokenVariable = "BACKER_LIVE_GIT_DEST_TOKEN";
    public const string AllowAdoptVariable = "BACKER_LIVE_GIT_ALLOW_ADOPT";

    public LiveGitFactAttribute()
    {
        Skip = ComputeSkipReason();
    }

    /// <summary>
    /// Null when the live smoke test is fully configured, otherwise the reason it is skipped.
    /// </summary>
    public static string? ComputeSkipReason()
    {
        if (Get(EnableVariable) != "1")
        {
            return $"Live git-mirror smoke test is opt-in and disabled. Set {EnableVariable}=1, " +
                   $"{SourceUrlVariable}, {DestinationUrlVariable} and {DestinationTokenVariable} " +
                   "(plus source/destination usernames as needed) to enable it. It runs a real Copy " +
                   "mirror between two real git remotes and PUSHES to the destination - see " +
                   $"{nameof(LiveGitFactAttribute)}'s doc comment for the full environment contract.";
        }

        if (string.IsNullOrWhiteSpace(SourceUrl))
        {
            return $"{EnableVariable}=1 but {SourceUrlVariable} is not set - it must be the source " +
                   "repository's clone URL (e.g. https://github.com/owner/repo).";
        }

        if (string.IsNullOrWhiteSpace(DestinationUrl))
        {
            return $"{EnableVariable}=1 but {DestinationUrlVariable} is not set - it must be an EXISTING " +
                   "destination repository's clone URL that this test is allowed to push to.";
        }

        if (string.IsNullOrWhiteSpace(DestinationToken))
        {
            return $"{EnableVariable}=1 but {DestinationTokenVariable} is not set - the mirror pushes to " +
                   "the destination and needs a write-scoped token even when the source is public.";
        }

        return null;
    }

    public static string? SourceUrl => Get(SourceUrlVariable);

    public static string SourceUsername => Get(SourceUserVariable) ?? string.Empty;

    public static string SourceToken => Get(SourceTokenVariable) ?? string.Empty;

    public static string? DestinationUrl => Get(DestinationUrlVariable);

    public static string DestinationUsername => Get(DestinationUserVariable) ?? string.Empty;

    public static string DestinationToken => Get(DestinationTokenVariable) ?? string.Empty;

    public static bool AllowAdopt => Get(AllowAdoptVariable) == "1";

    private static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
