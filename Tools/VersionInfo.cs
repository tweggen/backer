using System.Reflection;

namespace Tools;

/// <summary>
/// Provides version information about the application, embedded at build time.
/// </summary>
public class VersionInfo
{
    public string? GitCommitShort { get; set; }
    public string? GitCommitFull { get; set; }
    public string? GitBranch { get; set; }
    public DateTime BuildTime { get; set; }

    /// <summary>
    /// Gets version information that was embedded into the assembly at build time
    /// via AssemblyMetadata attributes (see Directory.Build.props).
    /// </summary>
    public static VersionInfo GetCurrent()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value);

        var info = new VersionInfo
        {
            GitCommitShort = metadata.GetValueOrDefault("GitCommitShort"),
            GitCommitFull = metadata.GetValueOrDefault("GitCommitFull"),
            GitBranch = metadata.GetValueOrDefault("GitBranch"),
        };

        if (metadata.TryGetValue("BuildTimestamp", out var ts) &&
            DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var buildTime))
        {
            info.BuildTime = buildTime;
        }

        return info;
    }
}
