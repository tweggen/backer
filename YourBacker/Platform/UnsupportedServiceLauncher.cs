namespace YourBacker.Platform;

/// <summary>
/// Fallback for platforms where service launching is not (yet) implemented.
/// <see cref="IsSupported"/> returns false so the UI can hide or disable
/// the "Launch Service" option.
/// </summary>
public class UnsupportedServiceLauncher : IServiceLauncher
{
    public bool IsSupported => false;

    public Task<bool> TryLaunchAsync()
    {
        return Task.FromResult(false);
    }
}
