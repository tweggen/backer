using Hannibal.Models;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers.Local;

/// <summary>
/// Storage provider for local filesystem paths
/// </summary>
public class LocalProvider : StorageProviderBase
{
    public override string Technology => "local";
    public override bool RequiresOAuth => false;

    public LocalProvider(ILogger<LocalProvider> logger) 
        : base(logger) 
    { 
    }

    /// <inheritdoc />
    public override Task<Dictionary<string, string>> BuildRCloneParametersAsync(
        StorageState state, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["type"] = "local"
        };

        // Local storage doesn't require additional parameters
        // The path is specified in the sync/copy command itself

        return Task.FromResult(parameters);
    }

    /// <inheritdoc />
    public override ValidationResult Validate(Storage storage)
    {
        var baseResult = base.Validate(storage);
        if (!baseResult.IsValid) return baseResult;

        // For local storage, the UriSchema should be a valid local path
        var path = storage.UriSchema;
        
        // Basic validation - more thorough validation would check if path exists
        if (path.Contains(".."))
        {
            return ValidationResult.Failure("Local path should not contain '..'");
        }

        return ValidationResult.Success();
    }
}
