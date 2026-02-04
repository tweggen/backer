namespace WorkerRClone.Models;

/// <summary>
/// Aggregate transfer statistics from rclone for overall progress display
/// </summary>
public class OverallTransferStats
{
    /// <summary>
    /// Total bytes transferred so far across all files
    /// </summary>
    public long BytesTransferred { get; set; }

    /// <summary>
    /// Total bytes to transfer (all files combined)
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Overall transfer speed in bytes per second
    /// </summary>
    public double Speed { get; set; }

    /// <summary>
    /// Estimated seconds until all transfers complete (null if unknown)
    /// </summary>
    public double? EtaSeconds { get; set; }

    /// <summary>
    /// Elapsed time in seconds since transfers started
    /// </summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>
    /// Number of files completed
    /// </summary>
    public int FilesCompleted { get; set; }

    /// <summary>
    /// Total number of files to transfer
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Number of errors encountered
    /// </summary>
    public int Errors { get; set; }

    /// <summary>
    /// Calculated overall progress percentage (0-100)
    /// </summary>
    public double ProgressPercent => TotalBytes > 0
        ? (double)BytesTransferred / TotalBytes * 100.0
        : 0;

    /// <summary>
    /// True if there are active transfers
    /// </summary>
    public bool HasActiveTransfers => TotalFiles > 0 || BytesTransferred < TotalBytes;
}
