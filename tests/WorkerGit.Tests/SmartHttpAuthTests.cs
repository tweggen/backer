using System.Text;
using FluentAssertions;
using WorkerGit.Services;
using WorkerGit.Tests.TestSupport;
using Xunit;

namespace WorkerGit.Tests;

/**
 * Gate D AC7: a Copy mirror against a token-authenticated real git
 * smart-HTTP server (<see cref="SmartHttpGitServer"/>) must never let the
 * token appear anywhere but the askpass helper's environment.
 */
public sealed class SmartHttpAuthTests : IDisposable
{
    private readonly GitTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task AC7_TokenNeverLeaksIntoCacheConfigArgvOrLog()
    {
        const string username = "backer-test-user";
        const string token = "s3cr3t-personal-access-token-DO-NOT-LEAK";

        var reposRoot = _env.NewPath("repos");
        Directory.CreateDirectory(reposRoot);
        var sourceBarePath = Path.Combine(reposRoot, "source.git");
        var destinationBarePath = Path.Combine(reposRoot, "dest.git");
        GitTestRepo.InitBare(sourceBarePath);
        GitTestRepo.InitBare(destinationBarePath);
        // git's own safety default: receive-pack (push) over smart HTTP is
        // off per-repo unless explicitly enabled.
        GitTestRepo.Run(destinationBarePath, "config", "http.receivepack", "true");

        var work = _env.NewPath("source-work");
        GitTestRepo.CloneToWorkdir(sourceBarePath, work);
        GitTestRepo.CreateOrphanBranch(work, "main");
        GitTestRepo.Commit(work, "a.txt", "hello", "init");
        GitTestRepo.Push(work, "refs/heads/*:refs/heads/*");

        await using var server = await SmartHttpGitServer.StartAsync(reposRoot, username, token);

        var trace = new RecordingGitCommandTrace();
        var runner = new GitCliRunner(_env.Options, trace);
        var cacheManager = new GitCacheManager(_env.Options, runner);
        var engine = new GitMirrorEngine(_env.Options, runner, cacheManager);

        var progressLog = new StringBuilder();
        var request = new GitMirrorRequest
        {
            Source = new GitMirrorEndpoint
            {
                RemoteUrl = server.RepoUrl("source.git"), Username = username, Token = token
            },
            Destination = new GitMirrorEndpoint
            {
                RemoteUrl = server.RepoUrl("dest.git"), Username = username, Token = token
            },
            Operation = GitMirrorOperation.Copy,
            UserId = "user1",
            SourceUriSchema = "http",
            OnProgress = line =>
            {
                lock (progressLog) { progressLog.AppendLine(line); }
            }
        };

        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(GitMirrorOutcome.Success, result.Message);
        GitTestRepo.ForEachRef(destinationBarePath).Should().ContainKey("refs/heads/main");

        // --- the point of AC7: explicit greps, not eyeballing ---

        foreach (var file in Directory.EnumerateFiles(_env.CacheRoot, "*", SearchOption.AllDirectories))
        {
            _containsToken(File.ReadAllBytes(file), token).Should()
                .BeFalse($"the cache file '{file}' must never contain the token");
        }

        foreach (var configFile in Directory.EnumerateFiles(reposRoot, "config", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(_env.CacheRoot, "config", SearchOption.AllDirectories)))
        {
            File.ReadAllText(configFile).Should().NotContain(token, $"'{configFile}' must never contain the token");
        }

        trace.Records.SelectMany(r => r.Arguments)
            .Should().NotContain(a => a.Contains(token, StringComparison.Ordinal));
        trace.Records.Should().NotContain(r => (r.Message ?? string.Empty).Contains(token, StringComparison.Ordinal));

        progressLog.ToString().Should().NotContain(token);
    }

    /** Binary-safe substring search - file content is not guaranteed to be valid UTF-8 text (packfiles are not). */
    private static bool _containsToken(byte[] haystack, string token)
    {
        var needle = Encoding.UTF8.GetBytes(token);
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var isMatch = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    isMatch = false;
                    break;
                }
            }

            if (isMatch)
            {
                return true;
            }
        }

        return false;
    }
}
