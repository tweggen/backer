using FluentAssertions;
using Hannibal.Models;

namespace Hannibal.Tests.Models;

/// <summary>
/// Gate A AC1: the predicate that keeps non-rclone storages out of
/// rclone.conf (docs/plan-git-repo-storage.md, RCloneService.cs filter).
/// </summary>
public class TechnologiesTests
{
    [Theory]
    [InlineData("onedrive")]
    [InlineData("dropbox")]
    [InlineData("googledrive")]
    [InlineData("nextcloud")]
    [InlineData("smb")]
    [InlineData("local")]
    public void IsRCloneTechnology_TrueForEachExistingTechnology(string technology)
    {
        Technologies.IsRCloneTechnology(technology).Should().BeTrue();
    }

    [Theory]
    [InlineData("git")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GIT")]
    [InlineData("unknown")]
    public void IsRCloneTechnology_FalseForGitAndUnknowns(string? technology)
    {
        Technologies.IsRCloneTechnology(technology).Should().BeFalse();
    }

    [Fact]
    public void GetTechnologies_IncludesGit()
    {
        Technologies.GetTechnologies().Should().Contain("git");
    }
}
