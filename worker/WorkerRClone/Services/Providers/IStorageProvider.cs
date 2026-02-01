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
/// Result of token validation and optional refresh
/// </summary>
public record TokenValidationResult(
    bool WasValid,
    bool WasRefreshed,
    bool IsNowValid,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Tokens were already valid, no refresh needed
    /// </summary>
    public static TokenValidationResult AlreadyValid() => new(true, false, true);
    
    /// <summary>
    /// Tokens were expired but successfully refreshed
    /// </summary>
    public static TokenValidationResult RefreshedSuccessfully() => new(false, true, true);
    
    /// <summary>
    /// Token refresh failed
    /// </summary>
    public static TokenValidationResult RefreshFailed(string errorMessage) => new(false, true, false, errorMessage);
    
    /// <summary>
    /// Provider doesn't use tokens (e.g., local storage, SMB with credentials)
    /// </summary>
    public static TokenValidationResult NotApplicable() => new(true, false, true);
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
    
    /// <summary>
    /// Check if tokens are valid and refresh them if they are expired or about to expire.
    /// Call this immediately before starting an rclone job to ensure tokens are fresh.
    /// </summary>
    /// <param name="state">The storage state containing the storage and OAuth client</param>
    /// <param name="bufferTime">Time buffer before actual expiry to trigger refresh (default: 5 minutes)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating whether tokens were valid, refreshed, or failed</returns>
    Task<TokenValidationResult> EnsureTokensValidAsync(
        StorageState state, 
        TimeSpan? bufferTime = null,
        CancellationToken cancellationToken = default);
}
