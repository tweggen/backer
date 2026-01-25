namespace YourBacker;

/// <summary>
/// Raw transfer stats data - matches the original WPF version
/// </summary>
public class FileTransferStats
{
    public string Id { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public double Progress { get; set; }
    public double Speed { get; set; }
    public long Size { get; set; }
    public string State { get; set; } = "";
}
