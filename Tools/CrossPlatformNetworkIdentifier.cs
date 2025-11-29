using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Tools;
public static class CrossPlatformNetworkIdentifier
{
    public static string GetCurrentNetwork()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetWindowsNetwork();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return GetLinuxNetwork();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return GetMacNetwork();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID")))
            return GetAndroidNetwork();

        return "Unknown";
    }

    private static string GetWindowsNetwork()
    {
        var active = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => ni.OperationalStatus == OperationalStatus.Up);
        return active?.Name ?? "No active interface";

        // For Wi-Fi SSID: WMI query via System.Management if needed
    }

    private static string GetLinuxNetwork()
    {
        return RunCommand("nmcli -t -f active,ssid dev wifi")
            ?.Split('\n')
            .FirstOrDefault(l => l.StartsWith("yes:"))
            ?.Split(':')[1]
            ?? "No active Wi-Fi";
    }

    private static string GetMacNetwork()
    {
        return RunCommand("/System/Library/PrivateFrameworks/Apple80211.framework/Versions/Current/Resources/airport -I")
            ?.Split('\n')
            .FirstOrDefault(l => l.Trim().StartsWith("SSID"))
            ?.Split(':')[1].Trim()
            ?? "No active Wi-Fi";
    }

    private static string GetAndroidNetwork()
    {
        // Requires Xamarin/MAUI interop with Android WifiManager
        return "Android SSID via WifiManager (interop required)";
    }

    private static string RunCommand(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("bash", $"-c \"{command}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            return process?.StandardOutput.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}