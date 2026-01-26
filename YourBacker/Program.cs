using System.Runtime.InteropServices;
using Avalonia;

namespace YourBacker;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // On macOS, hide the dock icon (LSUIElement behavior)
        // This is needed when running outside of an app bundle (e.g., during development)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            HideDockIconOnMac();
        }
        
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void HideDockIconOnMac()
    {
        try
        {
            // P/Invoke to set the activation policy to "accessory" (no dock icon)
            var nsApp = objc_getClass("NSApplication");
            var sharedApp = objc_msgSend(nsApp, sel_registerName("sharedApplication"));
            objc_msgSend(sharedApp, sel_registerName("setActivationPolicy:"), 1); // 1 = NSApplicationActivationPolicyAccessory
        }
        catch
        {
            // Ignore errors - this is a best-effort enhancement
        }
    }

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_getClass(string className);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr sel_registerName(string selectorName);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, long arg1);
}
