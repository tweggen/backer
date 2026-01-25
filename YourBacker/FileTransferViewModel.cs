using CommunityToolkit.Mvvm.ComponentModel;

namespace YourBacker;

/// <summary>
/// View model for individual file transfer - uses CommunityToolkit.Mvvm for cleaner property change notifications
/// </summary>
public partial class FileTransferViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = "";

    [ObservableProperty]
    private string _sourcePath = "";

    [ObservableProperty]
    private string _destinationPath = "";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private double _speed;

    [ObservableProperty]
    private long _size;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private DateTime _lastUpdated;

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
    /// Human-readable size string
    /// </summary>
    public string SizeFormatted
    {
        get
        {
            if (Size < 1024)
                return $"{Size} B";
            if (Size < 1024 * 1024)
                return $"{Size / 1024.0:F1} KB";
            if (Size < 1024 * 1024 * 1024)
                return $"{Size / (1024.0 * 1024):F1} MB";
            return $"{Size / (1024.0 * 1024 * 1024):F2} GB";
        }
    }

    public FileTransferViewModel()
    {
    }

    public FileTransferViewModel(FileTransferViewModel other)
    {
        Id = other.Id;
        SourcePath = other.SourcePath;
        DestinationPath = other.DestinationPath;
        Progress = other.Progress;
        Speed = other.Speed;
        Size = other.Size;
        State = other.State;
        LastUpdated = other.LastUpdated;
    }

    partial void OnSpeedChanged(double value)
    {
        OnPropertyChanged(nameof(SpeedFormatted));
    }

    partial void OnSizeChanged(long value)
    {
        OnPropertyChanged(nameof(SizeFormatted));
    }
}
