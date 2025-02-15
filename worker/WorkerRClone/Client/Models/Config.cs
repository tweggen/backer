namespace WorkerRClone.Client.Models;

public class Config
{
    // "Exclude": ["backup/**", "**/tmp/**"]
    //   "Exclude": ["directory1/**", "path/to/directory2/**"]
    // The exclude patterns follow the same syntax as regular rclone commands:
    //
    // Use ** to match any number of directories
    //    Use * to match any characters except /
    // Patterns are matched relative to the root of the transfer
    // Multiple patterns can be specified in the array
    public List<string> Exclude { get; set;} = new();
}