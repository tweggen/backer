namespace Tools;

/// <summary>
/// Provides version information about the application
/// </summary>
public class VersionInfo
{
    public string? GitCommitShort { get; set; }
    public string? GitCommitFull { get; set; }
    public string? GitBranch { get; set; }
    public DateTime BuildTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Attempts to get git version information from the working directory
    /// </summary>
    public static VersionInfo GetCurrent()
    {
        var info = new VersionInfo();

        try
        {
            // Get short commit hash
            var shortResult = System.Diagnostics.ProcessStartInfo.GetCurrentProcess()?.StartInfo;
            var shortProcess = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --short HEAD",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };

            using (var process = System.Diagnostics.Process.Start(shortProcess))
            {
                if (process != null)
                {
                    info.GitCommitShort = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);
                }
            }

            // Get full commit hash
            var fullProcess = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };

            using (var process = System.Diagnostics.Process.Start(fullProcess))
            {
                if (process != null)
                {
                    info.GitCommitFull = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);
                }
            }

            // Get branch name
            var branchProcess = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --abbrev-ref HEAD",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };

            using (var process = System.Diagnostics.Process.Start(branchProcess))
            {
                if (process != null)
                {
                    info.GitBranch = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);
                }
            }
        }
        catch (Exception)
        {
            // Git command failed or git not installed - info will have nulls
        }

        return info;
    }
}
