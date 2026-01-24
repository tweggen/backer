using Hannibal.Models;
using Microsoft.Extensions.Logging;
using WorkerRClone.Services.Utils;

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
    /// Rclone expects passwords in config files to be obscured using its own algorithm.
    /// </summary>
    /// <param name="storage">The storage containing the password</param>
    /// <returns>The obscured password suitable for rclone config files</returns>
    protected virtual string GetPasswordForRClone(Storage storage)
    {
        if (string.IsNullOrWhiteSpace(storage.Password))
        {
            return string.Empty;
        }
        
        return RClonePasswordObscurer.Obscure(storage.Password);
    }
}
