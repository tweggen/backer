using System;
using System.IO;
using System.Diagnostics;

namespace Tools;

public static class EnvironmentDetector
{
    public static bool IsDevelopment = false;
    
    // ------------------------------------------------------------
    // Existing methods unchanged
    // ------------------------------------------------------------

    public static bool IsInteractiveDev()
    {
        return Debugger.IsAttached
               || Environment.UserInteractive
               || IsDevelopment;
    }

    public static string GetUserLogDir(string appName)
    {
        if (OperatingSystem.IsWindows())
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, appName, "Logs");
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Logs", appName);
        }

        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgStateHome))
            return Path.Combine(xdgStateHome, appName, "Logs");

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localState = Path.Combine(homeDir, ".local", "state", appName, "Logs");
        var localShare = Path.Combine(homeDir, ".local", "share", appName, "Logs");

        return DirectoryExistsOrCreatable(localState) ? localState : localShare;
    }

    public static string GetServiceLogDir(string appName)
    {
        if (OperatingSystem.IsWindows())
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(baseDir, appName, "Logs");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine("/Library/Application Support", appName, "Logs");
        }

        var primary = Path.Combine("/var/log", appName);
        var fallback = Path.Combine("/var/lib", appName, "logs");

        return DirectoryExistsOrCreatable(primary) ? primary : fallback;
    }

    public static string GetLogDir(string appName)
    {
        return IsInteractiveDev() ? GetUserLogDir(appName) : GetServiceLogDir(appName);
    }

    public static string EnsureDirectory(string dir, string appName)
    {
        bool isService = !IsInteractiveDev();
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && isService)
            {
                var userDir = GetUserLogDir(appName);
                Directory.CreateDirectory(userDir);
                return userDir;
            }
            throw;
        }
    }

    private static bool DirectoryExistsOrCreatable(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return true;
            Directory.CreateDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------
    // NEW: Configuration directory helpers
    // ------------------------------------------------------------

    /// <summary>
    /// Returns a user-scoped configuration directory appropriate for each OS.
    /// Intended for JSON/text config files that should persist and be user-editable.
    /// </summary>
    public static string GetUserConfigDir(string appName)
    {
        if (OperatingSystem.IsWindows())
        {
            // %APPDATA%\Backer\Config
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(baseDir, appName, "Config");
        }

        if (OperatingSystem.IsMacOS())
        {
            // ~/Library/Application Support/Backer/Config
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", appName, "Config");
        }

        // Linux / Unix:
        // Follow XDG_CONFIG_HOME, then ~/.config/<app>/Config
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            return Path.Combine(xdgConfigHome, appName);

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".config", appName);
    }

    /// <summary>
    /// Returns a service/system-scoped configuration directory appropriate for each OS.
    /// Intended for system-wide JSON/text config files.
    /// </summary>
    public static string GetServiceConfigDir(string appName)
    {
        if (OperatingSystem.IsWindows())
        {
            // %ProgramData%\Backer\Config
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(baseDir, appName, "Config");
        }

        if (OperatingSystem.IsMacOS())
        {
            // /Library/Application Support/Backer/Config
            return Path.Combine("/Library/Application Support", appName, "Config");
        }

        // Linux services:
        // Conventional: /etc/<app> for config
        // Fallback: /var/lib/<app>/config
        var primary = Path.Combine("/etc", appName);
        var fallback = Path.Combine("/var/lib", appName, "config");

        return DirectoryExistsOrCreatable(primary) ? primary : fallback;
    }

    /// <summary>
    /// Top-level chooser: returns the correct config directory based on interactive/dev vs service.
    /// </summary>
    public static string GetConfigDir(string appName)
    {
        return IsInteractiveDev() ? GetUserConfigDir(appName) : GetServiceConfigDir(appName);
    }
}
