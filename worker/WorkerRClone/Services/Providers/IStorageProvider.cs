using Hannibal.Models;

namespace WorkerRClone.Services.Providers;

/// <summary>
/// Validation result for storage configuration
/// </summary>
public record ValidationResult(bool IsValid, string? ErrorMessage = null)
{
    /// <summary>
    /// Create a successful validation result
    /// </summary>
    public static ValidationResult Success() => new(true);
    
    /// <summary>
    /// Create a failed validation result with error message
    /// </summary>
    public static ValidationResult Failure(string errorMessage) => new(false, errorMessage);
}

/// <summary>
/// Interface for storage providers that handle different cloud/local storage technologies
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// The technology identifier (e.g., "dropbox", "onedrive", "googledrive", "smb")
    /// </summary>
    string Technology { get; }
    
    /// <summary>
    /// Whether this provider requires OAuth authentication
    /// </summary>
    bool RequiresOAuth { get; }
    
    /// <summary>
    /// Initialize the storage state with provider-specific configuration
    /// </summary>
    Task InitializeAsync(StorageState state, CancellationToken cancellationToken);
    
    /// <summary>
    /// Refresh tokens if needed (OAuth providers only)
    /// </summary>
    Task RefreshTokensAsync(StorageState state, CancellationToken cancellationToken);
    
    /// <summary>
    /// Build the rclone parameters dictionary
    /// </summary>
    Task<Dictionary<string, string>> BuildRCloneParametersAsync(
        StorageState state, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Validate that the storage has all required configuration
    /// </summary>
    ValidationResult Validate(Storage storage);
}
