using Hannibal.Services;

namespace Hannibal.Models;

/// <summary>
/// Request model for importing configuration
/// </summary>
public class ConfigImportRequest
{
    /// <summary>
    /// JSON string containing the exported configuration
    /// </summary>
    public string ConfigJson { get; set; } = "";
    
    /// <summary>
    /// Strategy for handling existing items during import
    /// </summary>
    public MergeStrategy MergeStrategy { get; set; } = MergeStrategy.SkipExisting;
}
