namespace BackerControl;


public class FileTransferStats
{
    public string Id { get; set; } // unique identifier
    public string SourcePath { get; set; }
    public string DestinationPath { get; set; }
    public double Progress { get; set; }
    public double Speed { get; set; }
    public long Size { get; set; }
    public string State { get; set; }
}