namespace YourBacker.Platform;

/// <summary>
/// Platform abstraction for launching (starting) the BackerAgent service.
/// Some platforms may not support launching a service from user-space,
/// in which case <see cref="IsSupported"/> returns false.
/// </summary>
public interface IServiceLauncher
{
    /// <summary>
    /// Whether this platform supports launching the service from YourBacker.
    /// When false, the UI should not offer a "Launch Service" option.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Attempts to start the BackerAgent service.
    /// On Windows this triggers a UAC elevation prompt via sc.exe.
    /// </summary>
    /// <returns>
    /// True if the launch command was issued successfully (the user confirmed
    /// the elevation prompt and the process started without error).
    /// False if the user cancelled the elevation prompt or an error occurred.
    /// </returns>
    Task<bool> TryLaunchAsync();
}
