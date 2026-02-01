using System.Diagnostics;
using System.Runtime.Versioning;

namespace YourBacker.Platform;

/// <summary>
/// macOS implementation: starts the BackerAgent launchd service via
/// <c>launchctl load</c> / <c>launchctl start</c>.
///
/// Expects the agent to be installed as a LaunchDaemon plist at
/// <c>/Library/LaunchDaemons/com.backer.agent.plist</c> (system-wide)
/// or as a LaunchAgent at <c>~/Library/LaunchAgents/com.backer.agent.plist</c>.
///
/// If the plist is a LaunchDaemon (system-wide), the launchctl bootstrap
/// command requires root, so we wrap it in <c>osascript</c> to prompt the
/// user for their password (macOS equivalent of UAC).
/// </summary>
[SupportedOSPlatform("macos")]
public class MacServiceLauncher : IServiceLauncher
{
    private const string DaemonLabel = "com.backer.agent";
    private const string DaemonPlistPath = "/Library/LaunchDaemons/com.backer.agent.plist";
    private const string AgentPlistPath = "~/Library/LaunchAgents/com.backer.agent.plist";

    public bool IsSupported => true;

    public Task<bool> TryLaunchAsync()
    {
        try
        {
            // Prefer the user-level LaunchAgent (no elevation required)
            var expandedAgentPath = AgentPlistPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (File.Exists(expandedAgentPath))
            {
                return Task.FromResult(RunLaunchctl($"load -w {expandedAgentPath}", elevated: false));
            }

            // Fall back to system-level LaunchDaemon (requires elevation)
            if (File.Exists(DaemonPlistPath))
            {
                return Task.FromResult(RunLaunchctl($"load -w {DaemonPlistPath}", elevated: true));
            }

            // No plist found — cannot launch
            Debug.WriteLine("No BackerAgent launchd plist found.");
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch service on macOS: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    private static bool RunLaunchctl(string arguments, bool elevated)
    {
        try
        {
            ProcessStartInfo psi;

            if (elevated)
            {
                // Use osascript to prompt for admin password (macOS "UAC")
                var script = $"do shell script \"launchctl {arguments}\" with administrator privileges";
                psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    Arguments = $"-e '{script}'",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "/bin/launchctl",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            process.WaitForExit(TimeSpan.FromSeconds(15));
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"launchctl error: {ex.Message}");
            return false;
        }
    }
}
