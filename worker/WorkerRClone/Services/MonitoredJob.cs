using Hannibal.Models;

namespace WorkerRClone.Services;

/// <summary>
/// Wraps a Job with monitoring state for timeout detection.
/// Tracks activity to detect stalled jobs with expired OAuth2 tokens.
/// </summary>
public class MonitoredJob
{
    /// <summary>
    /// The job being monitored
    /// </summary>
    public required Job Job { get; init; }

    /// <summary>
    /// When the job was started
    /// </summary>
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Last time any activity was detected (bytes transferred changed)
    /// </summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total bytes transferred at last check (to detect progress)
    /// </summary>
    public long LastBytesTransferred { get; set; } = 0;

    /// <summary>
    /// Whether the source endpoint uses OAuth2
    /// </summary>
    public bool SourceIsOAuth2 { get; init; }

    /// <summary>
    /// Whether the destination endpoint uses OAuth2
    /// </summary>
    public bool DestinationIsOAuth2 { get; init; }

    /// <summary>
    /// Check if this job involves any OAuth2 endpoint
    /// </summary>
    public bool HasOAuth2Endpoint => SourceIsOAuth2 || DestinationIsOAuth2;
}
