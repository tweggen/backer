using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;


public static class CrossPlatformProcessManager
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool IsUnix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static Process StartManagedProcess(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to start process");

        //#if WINDOWS
        if (IsWindows)
        {
            JobManager.AddProcess(process);
        }
        //#endif
        //#if LINUX || OSX
        if (IsUnix)
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try
                {
                    // Kill entire process group (negative PID)
                    Process.Start("kill", $"-TERM -{process.Id}");
                }
                catch { /* best effort */ }
            };
        }
        //#endif

        return process;
    }
}