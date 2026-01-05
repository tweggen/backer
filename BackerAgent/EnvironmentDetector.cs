using System;
using System.IO;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;

public static class LogPathHelper
{
    /// <summary>
    /// Decide whether we’re in an interactive/dev session.
    /// Uses Debugger, Environment.UserInteractive, and ASP.NET Core environment.
    /// </summary>
    public static bool IsInteractiveDev(IHostEnvironment? env = null)
    {
        // Environment.UserInteractive is reliable on Windows. On other OSes it’s a hint.
        // Debugger.IsAttached is the clearest signal for an interactive debug session.
        return Debugger.IsAttached
               || Environment.UserInteractive
               || (env?.IsDevelopment() ?? false);
    }

    /// <summary>
    /// Returns a user-scoped log directory appropriate for each OS.
    /// Intended for interactive/dev runs and per-user agents.
    /// </summary>
    public static string GetUserLogDir(string appName)
    {
        if (OperatingSystem.IsWindows())
        {
            // %LOCALAPPDATA%\Backer\Logs
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, appName, "Logs");
        }

        if (OperatingSystem.IsMacOS())
        {
            // ~/Library/Logs/Backer
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Logs", appName);
        }

        // Linux / other Unix:
        // Prefer XDG_STATE_HOME (state/log-ish), then ~/.local/state, then ~/.local/share.
        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgStateHome))
        {
            return Path.Combine(xdgStateHome, appName, "Logs");
        }

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localState = Path.Combine(homeDir, ".local", "state", appName, "Logs");
        var localShare = Path.Combine(homeDir, ".local", "share", appName, "Logs");

        // Use ~/.local/state if possible; otherwise fallback to ~/.local/share.
        return DirectoryExistsOrCreatable(localState) ? localState : localShare;
    }

    /// <summary>
    /// Returns a service/system-scoped log directory appropriate for each OS.
    /// Intended for Windows Services, macOS launchd daemons, and Linux systemd services.
    /// </summary>
    public static string GetServiceLogDir(string appName)
    {
        if (OperatingSystem.IsWindows())
        {
            // %ProgramData%\Backer\Logs
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(baseDir, appName, "Logs");
        }

        if (OperatingSystem.IsMacOS())
        {
            // Prefer /Library/Application Support/Backer/Logs (writable with proper ACLs)
            // Some admins prefer /Library/Logs/Backer; choose one policy and be consistent.
            return Path.Combine("/Library/Application Support", appName, "Logs");
        }

        // Linux services: conventional location is /var/log/<app>
        // If not writable by the service user, consider /var/lib/<app>/logs instead.
        var primary = Path.Combine("/var/log", appName);
        var fallback = Path.Combine("/var/lib", appName, "logs");

        return DirectoryExistsOrCreatable(primary) ? primary : fallback;
    }

    /// <summary>
    /// Top-level chooser: returns the correct log directory based on interactive/dev vs service.
    /// </summary>
    public static string GetLogDir(string appName, bool isInteractiveDev)
    {
        return isInteractiveDev ? GetUserLogDir(appName) : GetServiceLogDir(appName);
    }

    /// <summary>
    /// Best-effort creation of the directory (and parents). Returns the final path chosen.
    /// For Linux services, will auto-fallback to user dir if service dir is not creatable.
    /// </summary>
    public static string EnsureDirectory(string dir, string appName, bool isService)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            // On Linux, a service user may not have permissions to /var/log/<app>.
            // Fallback to a user-scoped directory to avoid crashing, especially in dev/test.
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && isService)
            {
                var userDir = GetUserLogDir(appName);
                Directory.CreateDirectory(userDir);
                return userDir;
            }
            throw; // rethrow elsewhere if you prefer to fail fast
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
}
