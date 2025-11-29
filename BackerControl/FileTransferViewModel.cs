using System.ComponentModel;
using System.IO;

namespace BackerControl;

public class FileTransferViewModel : INotifyPropertyChanged
{
    public string Id { get; set; }
    public string SourcePath { get; set; }
    public string DestinationPath { get; set; }
    public double Progress { get; set; }
    public double Speed { get; set; }
    public long Size { get; set; }
    public string State { get; set; }
    public DateTime LastUpdated { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
        PropertyChanged = other.PropertyChanged;
    }

    public FileTransferViewModel()
    {
        Id = "";
        SourcePath = "";
        DestinationPath = "";
        State = "";
    }
}