using System.ComponentModel;

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

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}