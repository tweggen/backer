using Hannibal.Models;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers.Local;

/// <summary>
/// Base class for credential-based (non-OAuth) storage providers like SMB, FTP, SFTP.
/// These providers use username/password authentication rather than OAuth.
/// </summary>
public abstract class CredentialStorageProviderBase : StorageProviderBase
{
    public override bool RequiresOAuth => false;

    protected CredentialStorageProviderBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>
    /// Credential-based providers don't need token refresh
    /// </summary>
    public override Task RefreshTokensAsync(StorageState state, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Validate credential-based requirements
    /// </summary>
    public override ValidationResult Validate(Storage storage)
    {
        var baseResult = base.Validate(storage);
        if (!baseResult.IsValid) 
            return baseResult;

        if (string.IsNullOrWhiteSpace(storage.Host))
            return ValidationResult.Failure($"Host is required for {Technology}");

        // Username and password may be optional for some providers (e.g., anonymous access)
        // Specific providers can override to make them required

        return ValidationResult.Success();
    }

    /// <summary>
    /// Obscure a password for rclone configuration.
    /// Rclone expects passwords to be obscured using its own algorithm.
    /// For now, we pass the password as-is and rely on rclone's --obscure flag
    /// or assume the password is already obscured in the database.
    /// </summary>
    /// <remarks>
    /// In production, you might want to:
    /// 1. Store already-obscured passwords in the database
    /// 2. Call rclone obscure command to obscure passwords
    /// 3. Use rclone's built-in password handling
    /// </remarks>
    protected virtual string GetPasswordForRClone(Storage storage)
    {
        // IMPORTANT: In production, passwords should be obscured using rclone's algorithm
        // This is a placeholder - implement proper password obscuring
        return storage.Password;
    }
}
