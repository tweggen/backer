using System.Diagnostics;
using System.Runtime.Versioning;

namespace YourBacker.Platform;

/// <summary>
/// Windows implementation: starts the BackerAgent Windows Service via
/// <c>sc.exe start BackerAgent</c> with UAC elevation (<c>runas</c> verb).
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsServiceLauncher : IServiceLauncher
{
    private const string ServiceName = "BackerAgent";

    public bool IsSupported => true;

    public Task<bool> TryLaunchAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"start {ServiceName}",
                Verb = "runas",           // triggers UAC elevation prompt
                UseShellExecute = true,   // required for Verb = "runas"
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return Task.FromResult(false);

            process.WaitForExit(TimeSpan.FromSeconds(15));
            return Task.FromResult(process.ExitCode == 0);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user cancelled the UAC prompt
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch service: {ex.Message}");
            return Task.FromResult(false);
        }
    }
}
