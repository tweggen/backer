using FluentAssertions;
using Hannibal.Models;
using NSubstitute;
using TestSupport.Agent;
using Xunit;

namespace BackerAgent.IntegrationTests;

/// <summary>
/// Gate A AC5 (docs/plan-git-repo-storage.md): a git storage must never reach
/// backer-rclone.conf. RCloneStorages writes an empty section for any
/// technology its provider factory does not know (RCloneStorages.cs:68-74),
/// so an agent that iterated storages unfiltered would leak a blank
/// "[leaktestgit]" section into the file every boot. The filter under test is
/// Technologies.IsRCloneTechnology, applied in
/// RCloneService._toRCloneStorageList before the storage list is ever
/// assigned or iterated.
/// </summary>
public class RCloneConfigTechnologyFilterTests
{
    [Fact]
    public async Task TheAgentsRcloneConfig_HasTheRcloneStorageSection_AndNotTheGitOne()
    {
        using var factory = await AgentHostFactory.CreateAsync();

        factory.Hannibal
            .GetStoragesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Storage
                {
                    Technology = "local",
                    UriSchema = "leaktestlocal",
                    Networks = ""
                },
                new Storage
                {
                    Technology = "git",
                    UriSchema = "leaktestgit",
                    Host = "https://example.invalid/",
                    Networks = ""
                }
            }.AsEnumerable());

        await factory.StartAgentAsync();

        var configPath = Path.Combine(factory.RCloneConfigDirectory, "backer-rclone.conf");

        var content = await _waitForConfigContainingAsync(configPath, "[leaktestlocal]");

        content.Should().Contain("[leaktestlocal]",
            "a storage of a real rclone technology must still be written");
        content.Should().NotContain("leaktestgit",
            "a git storage must never appear in rclone.conf, in any form");
    }

    /// <summary>
    /// A ceiling on a condition, not a sleep: the config is (re)written during
    /// startup, asynchronously with respect to StartAgentAsync's own wait for
    /// the Running state.
    /// </summary>
    private static async Task<string> _waitForConfigContainingAsync(string path, string expectedSection)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                string? content = null;
                try
                {
                    content = await File.ReadAllTextAsync(path);
                }
                catch (IOException)
                {
                    // SaveToFile writes a temp file then copies over the real
                    // one; a read racing that copy is retried, not failed.
                }

                if (content is not null && content.Contains(expectedSection))
                {
                    return content;
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"backer-rclone.conf at '{path}' never contained '{expectedSection}' within the timeout.");
    }
}
