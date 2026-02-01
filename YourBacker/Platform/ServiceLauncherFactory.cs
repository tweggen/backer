using System.Runtime.InteropServices;

namespace YourBacker.Platform;

/// <summary>
/// Creates the appropriate <see cref="IServiceLauncher"/> for the current OS.
/// </summary>
public static class ServiceLauncherFactory
{
    public static IServiceLauncher Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsServiceLauncher();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacServiceLauncher();

        // Linux, etc. — not yet implemented
        return new UnsupportedServiceLauncher();
    }
}
