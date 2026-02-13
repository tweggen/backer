using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace YourBacker;

public partial class JobViewModel : ObservableObject
{
    [ObservableProperty]
    private int _hannibalJobId;

    [ObservableProperty]
    private string _tag = "";

    [ObservableProperty]
    private string _sourcePath = "";

    [ObservableProperty]
    private string _destinationPath = "";

    [ObservableProperty]
    private DateTime _startedAt;

    [ObservableProperty]
    private DateTime? _lastTransferActivity;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private string _statusText = "";

    public ObservableCollection<FileTransferViewModel> Transfers { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();
    public OverallProgressViewModel Progress { get; } = new();

    public string HeaderText => string.IsNullOrEmpty(Tag)
        ? $"{SourcePath} -> {DestinationPath}"
        : $"{Tag}: {SourcePath} -> {DestinationPath}";

    partial void OnTagChanged(string value) => OnPropertyChanged(nameof(HeaderText));
    partial void OnSourcePathChanged(string value) => OnPropertyChanged(nameof(HeaderText));
    partial void OnDestinationPathChanged(string value) => OnPropertyChanged(nameof(HeaderText));
}
