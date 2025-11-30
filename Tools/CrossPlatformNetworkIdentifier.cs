using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Tools;
public static class CrossPlatformNetworkIdentifier
{
    private static string? _withoutLinkInfo(string s)
    {
        var idx = s.IndexOf('%');
        if (idx >= 0)
        {
            return s.Substring(0, idx);
        }
        else
        {
            return s;
        }
    }
    
    private static string? GetPortableNetwork()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;

            var ipProps = ni.GetIPProperties();
            foreach (var addr in ipProps.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork) // IPv4
                {
                    Console.WriteLine($"Interface: {ni.Description}");
                    Console.WriteLine($"IP Address: {addr.Address}");
                    Console.WriteLine($"Subnet Mask: {addr.IPv4Mask}");
                }
            }

            string? ipv4GW = null, ipv6GW = null;
            foreach (var gw in ipProps.GatewayAddresses)
            {
                if (gw.Address.IsIPv6LinkLocal)
                {
                    ipv6GW = gw.Address.ToString();
                }

                switch (gw.Address.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        ipv4GW = gw.Address.ToString();
                        break;
                    case AddressFamily.InterNetworkV6:
                        ipv6GW = gw.Address.ToString();
                        break;
                    default:
                        break;
                }
                
                Console.WriteLine($"Gateway: {gw.Address}");
            }

            if (!String.IsNullOrWhiteSpace(ipv6GW))
            {
                return _withoutLinkInfo(ipv6GW);
            }

            if (!String.IsNullOrWhiteSpace(ipv4GW))
            {
                return _withoutLinkInfo(ipv4GW);
            }

        }

        return null;
    }
    
    public static string GetCurrentNetwork()
    {
        string? gw = GetPortableNetwork();
        if (gw != null)
        {
            return gw;
        }
        
        #if false
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetWindowsNetwork();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return GetLinuxNetwork();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return GetMacNetwork();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID")))
            return GetAndroidNetwork();
        #endif 
        
        return "Unknown";
    }

    
    public static string? GetWifiSsid()
    {
        try
        {
            var searcher = new ManagementObjectSearcher("root\\WMI",
                "SELECT * FROM MSNdis_80211_ServiceSetIdentifier");
            foreach (ManagementObject obj in searcher.Get())
            {
                var ssidBytes = (byte[])obj["Ndis80211SsId"];
                return Encoding.ASCII.GetString(ssidBytes);
            }
        }
        catch (Exception e)
        {
            
        }
        return null;
    }
    
    
    private static string GetWindowsNetwork()
    {
        #if false
        // Try SSID first
        var ssid = GetWifiSsid();
        if (!string.IsNullOrEmpty(ssid))
            return $"Wi-Fi SSID: {ssid}";

        // Fallback: network profile name
        var manager = new NetworkListManager() as INetworkListManager;
        foreach (INetwork network in manager!.GetNetworks(NLM_ENUM_NETWORK.NLM_ENUM_NETWORK_CONNECTED))
        {
            return $"Profile: {network.GetName()}";
        }

        // Fallback: adapter description
    #endif
        var active = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => ni.OperationalStatus == OperationalStatus.Up);
        return active?.Description ?? "Unknown network";
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