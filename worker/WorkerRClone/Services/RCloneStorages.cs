using Hannibal.Configuration;
using Hannibal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkerRClone.Configuration;
using WorkerRClone.Services.Providers;

namespace WorkerRClone.Services;

/// <summary>
/// Service to maintain a list of storage states and manage storage providers.
/// </summary>
public class RCloneStorages
{
    private readonly ILogger<RCloneStorages> _logger;
    private readonly IStorageProviderFactory _providerFactory;
    
    private readonly SortedDictionary<string, StorageState> _mapStorageStates = new();

    public RCloneStorages(
        ILogger<RCloneStorages> logger,
        IStorageProviderFactory providerFactory,
        IOptionsMonitor<RCloneServiceOptions> optionsMonitor)
    {
        _logger = logger;
        _providerFactory = providerFactory;

        optionsMonitor.OnChange(updated =>
        {
            _logger.LogInformation("RCloneStorages: Options changed, clearing storage states.");
            ClearStorageStates();
        });
    }

    /// <summary>
    /// Find or create a storage state for the given storage.
    /// </summary>
    public async Task<StorageState> FindStorageState(
        Storage storage,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (forceRefresh)
        {
            _mapStorageStates.Remove(storage.Technology);
        }

        if (_mapStorageStates.TryGetValue(storage.Technology, out var existing))
        {
            return existing;
        }

        var state = await CreateStorageStateAsync(storage, cancellationToken);
        _mapStorageStates[storage.Technology] = state;
        return state;
    }

    /// <summary>
    /// Create a new storage state using the appropriate provider.
    /// </summary>
    private async Task<StorageState> CreateStorageStateAsync(
        Storage storage, CancellationToken cancellationToken)
    {
        var state = new StorageState { Storage = storage };

        try
        {
            if (!_providerFactory.IsSupported(storage.Technology))
            {
                _logger.LogWarning($"Storage technology '{storage.Technology}' is not supported. " +
                    $"Supported: {string.Join(", ", _providerFactory.GetSupportedTechnologies())}");
                state.RCloneParameters = new SortedDictionary<string, string>();
                return state;
            }

            var provider = _providerFactory.GetProvider(storage.Technology);
            
            // Validate storage configuration
            var validation = provider.Validate(storage);
            if (!validation.IsValid)
            {
                _logger.LogWarning($"Storage validation failed for {storage.Technology}: {validation.ErrorMessage}");
                state.RCloneParameters = new SortedDictionary<string, string>();
                return state;
            }

            // Initialize the provider (sets up OAuth client, refreshes tokens, etc.)
            await provider.InitializeAsync(state, cancellationToken);
            
            // Build rclone parameters
            var parameters = await provider.BuildRCloneParametersAsync(state, cancellationToken);
            state.RCloneParameters = new SortedDictionary<string, string>(parameters);
        }
        catch (Exception e)
        {
            _logger.LogError(e, $"Error initializing provider for {storage.Technology}");
            state.RCloneParameters = new SortedDictionary<string, string>();
        }

        return state;
    }

    /// <summary>
    /// Clear all storage states, forcing them to be recreated with fresh tokens.
    /// </summary>
    public void ClearStorageStates()
    {
        _mapStorageStates.Clear();
        _logger.LogInformation("RCloneStorages: Cleared all storage states for reauth");
    }

    /// <summary>
    /// Get all supported storage technologies.
    /// </summary>
    public IEnumerable<string> GetSupportedTechnologies() => 
        _providerFactory.GetSupportedTechnologies();

    /// <summary>
    /// Check if a technology is supported.
    /// </summary>
    public bool IsSupported(string technology) => 
        _providerFactory.IsSupported(technology);
}
