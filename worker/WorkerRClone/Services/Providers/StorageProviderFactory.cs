namespace WorkerRClone.Services.Providers;

/// <summary>
/// Factory interface for resolving storage providers by technology
/// </summary>
public interface IStorageProviderFactory
{
    /// <summary>
    /// Get a storage provider for the specified technology
    /// </summary>
    IStorageProvider GetProvider(string technology);
    
    /// <summary>
    /// Get all supported technology identifiers
    /// </summary>
    IEnumerable<string> GetSupportedTechnologies();
    
    /// <summary>
    /// Check if a technology is supported
    /// </summary>
    bool IsSupported(string technology);
}

/// <summary>
/// Factory implementation that resolves storage providers from DI
/// </summary>
public class StorageProviderFactory : IStorageProviderFactory
{
    private readonly Dictionary<string, IStorageProvider> _providers;

    public StorageProviderFactory(IEnumerable<IStorageProvider> providers)
    {
        _providers = providers.ToDictionary(
            p => p.Technology, 
            p => p, 
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IStorageProvider GetProvider(string technology)
    {
        if (_providers.TryGetValue(technology, out var provider))
            return provider;

        throw new NotSupportedException($"Storage technology '{technology}' is not supported. " +
            $"Supported technologies: {string.Join(", ", _providers.Keys)}");
    }

    /// <inheritdoc />
    public IEnumerable<string> GetSupportedTechnologies() => _providers.Keys;

    /// <inheritdoc />
    public bool IsSupported(string technology) => 
        _providers.ContainsKey(technology);
}
