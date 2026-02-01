using Hannibal.Models;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers;

/// <summary>
/// Base class for storage providers with common functionality
/// </summary>
public abstract class StorageProviderBase : IStorageProvider
{
    protected readonly ILogger Logger;
    
    public abstract string Technology { get; }
    public abstract bool RequiresOAuth { get; }

    protected StorageProviderBase(ILogger logger)
    {
        Logger = logger;
    }

    /// <inheritdoc />
    public virtual Task InitializeAsync(StorageState state, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task RefreshTokensAsync(StorageState state, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task<TokenValidationResult> EnsureTokensValidAsync(
        StorageState state, 
        TimeSpan? bufferTime = null,
        CancellationToken cancellationToken = default)
    {
        // Non-OAuth providers don't have tokens to validate
        return Task.FromResult(TokenValidationResult.NotApplicable());
    }

    /// <inheritdoc />
    public abstract Task<Dictionary<string, string>> BuildRCloneParametersAsync(
        StorageState state, 
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public virtual ValidationResult Validate(Storage storage)
    {
        if (string.IsNullOrWhiteSpace(storage.UriSchema))
            return ValidationResult.Failure("UriSchema is required");
        return ValidationResult.Success();
    }
}
