using CommunityToolkit.Mvvm.ComponentModel;

namespace YourBacker;

/// <summary>
/// ViewModel for displaying overall transfer progress in the UI
/// </summary>
public partial class OverallProgressViewModel : ObservableObject
{
    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private long _bytesTransferred;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private double _speed;

    [ObservableProperty]
    private double? _etaSeconds;

    [ObservableProperty]
    private int _filesCompleted;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private int _errors;

    [ObservableProperty]
    private bool _hasActiveTransfers;

    /// <summary>
    /// Human-readable ETA string (e.g., "2m 30s", "1h 15m")
    /// </summary>
    public string EtaFormatted
    {
        get
        {
            if (!EtaSeconds.HasValue || EtaSeconds.Value <= 0)
                return "--";

            var eta = TimeSpan.FromSeconds(EtaSeconds.Value);
            if (eta.TotalHours >= 1)
                return $"{(int)eta.TotalHours}h {eta.Minutes}m";
            if (eta.TotalMinutes >= 1)
                return $"{(int)eta.TotalMinutes}m {eta.Seconds}s";
            return $"{eta.Seconds}s";
        }
    }

    /// <summary>
    /// Human-readable speed string
    /// </summary>
    public string SpeedFormatted
    {
        get
        {
            if (Speed < 1024)
                return $"{Speed:F0} B/s";
            if (Speed < 1024 * 1024)
                return $"{Speed / 1024:F1} KB/s";
            if (Speed < 1024 * 1024 * 1024)
                return $"{Speed / (1024 * 1024):F1} MB/s";
            return $"{Speed / (1024 * 1024 * 1024):F2} GB/s";
        }
    }

    /// <summary>
    /// Human-readable bytes transferred string (e.g., "1.5 GB / 10 GB")
    /// </summary>
    public string BytesFormatted => $"{FormatBytes(BytesTransferred)} / {FormatBytes(TotalBytes)}";

    /// <summary>
    /// File progress string (e.g., "5 / 100 files")
    /// </summary>
    public string FilesFormatted => $"{FilesCompleted} / {TotalFiles} files";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    partial void OnSpeedChanged(double value) => OnPropertyChanged(nameof(SpeedFormatted));
    partial void OnEtaSecondsChanged(double? value) => OnPropertyChanged(nameof(EtaFormatted));
    partial void OnBytesTransferredChanged(long value) => OnPropertyChanged(nameof(BytesFormatted));
    partial void OnTotalBytesChanged(long value) => OnPropertyChanged(nameof(BytesFormatted));
    partial void OnFilesCompletedChanged(int value) => OnPropertyChanged(nameof(FilesFormatted));
    partial void OnTotalFilesChanged(int value) => OnPropertyChanged(nameof(FilesFormatted));
}
