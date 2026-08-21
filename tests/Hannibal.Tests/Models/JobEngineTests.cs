using FluentAssertions;
using Hannibal.Models;

namespace Hannibal.Tests.Models;

/// <summary>
/// Gate B AC1 (docs/plan-git-repo-storage.md): the pure classifier that
/// decides which transfer engine a (source, destination) technology pair
/// needs, and the git remote URL normalizer used both by Gate B's
/// self-mirror rejection and later gates' run-time guard.
/// </summary>
public class JobEngineClassifierTests
{
    [Theory]
    [InlineData("onedrive", "dropbox")]
    [InlineData("smb", "local")]
    [InlineData("local", "local")]
    [InlineData("googledrive", "nextcloud")]
    public void Classify_TwoRcloneTechnologies_ReturnsRclone(string source, string destination)
    {
        JobEngineClassifier.Classify(source, destination).Should().Be(JobEngine.Rclone);
    }

    [Fact]
    public void Classify_GitAndGit_ReturnsGit()
    {
        JobEngineClassifier.Classify("git", "git").Should().Be(JobEngine.Git);
    }

    [Theory]
    [InlineData("git", "onedrive")]
    [InlineData("onedrive", "git")]
    public void Classify_MixedGitAndNonGit_ReturnsUnsupported(string source, string destination)
    {
        JobEngineClassifier.Classify(source, destination).Should().Be(JobEngine.Unsupported);
    }

    [Theory]
    [InlineData("unknown", "local")]
    [InlineData("local", "unknown")]
    [InlineData("git", "unknown")]
    [InlineData("unknown", "git")]
    [InlineData(null, "local")]
    [InlineData("local", null)]
    [InlineData(null, null)]
    public void Classify_UnknownTechnologyEitherSide_ReturnsUnsupported(string? source, string? destination)
    {
        JobEngineClassifier.Classify(source, destination).Should().Be(JobEngine.Unsupported);
    }

    [Fact]
    public void Classify_EndpointOverload_DelegatesToStorageTechnology()
    {
        var source = new Endpoint { Storage = new Storage { Technology = "git" }, Path = "a/b" };
        var destination = new Endpoint { Storage = new Storage { Technology = "git" }, Path = "c/d" };

        JobEngineClassifier.Classify(source, destination).Should().Be(JobEngine.Git);
    }
}

/// <summary>
/// Gate B AC1/AC4: the normalizer two endpoints must agree on for the
/// self-mirror guard to catch two different Storage rows pointing at the
/// same repository.
/// </summary>
public class GitRemoteUrlTests
{
    [Fact]
    public void Normalize_AppendsDotGit_WhenMissing()
    {
        GitRemoteUrl.Normalize("https://github.com/", "owner/repo")
            .Should().Be("https://github.com/owner/repo.git");
    }

    [Fact]
    public void Normalize_DoesNotDoubleAppendDotGit()
    {
        GitRemoteUrl.Normalize("https://github.com/", "owner/repo.git")
            .Should().Be("https://github.com/owner/repo.git");
    }

    [Fact]
    public void Normalize_DotGitSuffixIsCaseInsensitive()
    {
        GitRemoteUrl.Normalize("https://github.com/", "owner/repo.GIT")
            .Should().Be("https://github.com/owner/repo.GIT");
    }

    [Fact]
    public void Normalize_TrailingSlashOnHost_SameAsWithout()
    {
        var withSlash = GitRemoteUrl.Normalize("https://github.com/", "owner/repo");
        var withoutSlash = GitRemoteUrl.Normalize("https://github.com", "owner/repo");

        withSlash.Should().Be(withoutSlash);
    }

    [Fact]
    public void Normalize_HostCaseDifference_NormalizesEqual()
    {
        var lower = GitRemoteUrl.Normalize("https://github.com/", "owner/repo");
        var upperHost = GitRemoteUrl.Normalize("https://GITHUB.com", "owner/repo");

        lower.Should().Be(upperHost);
    }

    [Fact]
    public void Normalize_PathCaseDifference_IsNotEqual()
    {
        var lower = GitRemoteUrl.Normalize("https://github.com/", "owner/repo");
        var upperPath = GitRemoteUrl.Normalize("https://github.com/", "Owner/Repo");

        lower.Should().NotBe(upperPath);
    }

    [Fact]
    public void Normalize_FilesystemRootHost_TrailingSlashAndSeparatorStyleAgree()
    {
        var withSlash = GitRemoteUrl.Normalize(@"D:\backup\git\", "owner/repo");
        var withoutSlash = GitRemoteUrl.Normalize(@"D:\backup\git", @"owner\repo");

        withSlash.Should().Be(withoutSlash);
    }

    [Fact]
    public void Normalize_FilesystemRootHost_ProducesForwardSlashPath()
    {
        var normalized = GitRemoteUrl.Normalize(@"D:\backup\git", "owner/repo");

        normalized.Should().Be("D:/backup/git/owner/repo.git");
    }
}
