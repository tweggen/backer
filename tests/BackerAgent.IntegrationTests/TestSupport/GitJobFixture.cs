using Hannibal.Models;

namespace BackerAgent.IntegrationTests.TestSupport;

/// <summary>
/// Builds a <see cref="Job"/> pointed at two local bare repositories,
/// mirroring <c>tests/WorkerGit.Tests/GitWorkerServiceTests._buildJob</c> -
/// duplicated here (rather than shared) because this project does not
/// otherwise reference <c>tests/WorkerGit.Tests</c>.
/// </summary>
internal static class GitJobFixture
{
    public static Job Build(int id, string sourceBare, string destinationBare, Rule.RuleOperation operation)
    {
        var sourceStorage = new Storage
        {
            Id = 1, UserId = "user1", Technology = "git", UriSchema = "srcgit",
            Host = sourceBare, Username = "", Password = ""
        };
        var destinationStorage = new Storage
        {
            Id = 2, UserId = "user1", Technology = "git", UriSchema = "dstgit",
            Host = destinationBare, Username = "", Password = ""
        };

        var sourceEndpoint = new Endpoint
        {
            Id = 1, Name = "src", UserId = "user1", StorageId = 1, Storage = sourceStorage, Path = ""
        };
        var destinationEndpoint = new Endpoint
        {
            Id = 2, Name = "dst", UserId = "user1", StorageId = 2, Storage = destinationStorage, Path = ""
        };

        return new Job
        {
            Id = id,
            UserId = "user1",
            Tag = "test",
            Operation = operation,
            Owner = "",
            State = Job.JobState.Ready,
            SourceEndpointId = 1,
            SourceEndpoint = sourceEndpoint,
            DestinationEndpointId = 2,
            DestinationEndpoint = destinationEndpoint
        };
    }
}
