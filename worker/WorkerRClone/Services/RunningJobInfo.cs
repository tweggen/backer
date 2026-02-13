using Hannibal.Models;

namespace WorkerRClone.Services;

public class RunningJobInfo
{
    public required Job Job { get; init; }
    public int RCloneJobId { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastTransferActivity { get; set; }
    public List<string> Errors { get; } = new();
    public string LastSeenError { get; set; } = "";

    // OAuth2 inactivity monitoring (from MonitoredJob)
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public long LastBytesTransferred { get; set; } = 0;
    public bool SourceIsOAuth2 { get; init; }
    public bool DestinationIsOAuth2 { get; init; }
    public bool HasOAuth2Endpoint => SourceIsOAuth2 || DestinationIsOAuth2;
}
