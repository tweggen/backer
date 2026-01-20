using Hannibal.Models;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers.Local;

/// <summary>
/// Storage provider for SMB/CIFS network shares
/// </summary>
public class SmbProvider : StorageProviderBase
{
    public override string Technology => "smb";
    public override bool RequiresOAuth => false;

    public SmbProvider(ILogger<SmbProvider> logger) 
        : base(logger) 
    { 
    }

    /// <inheritdoc />
    public override Task<Dictionary<string, string>> BuildRCloneParametersAsync(
        StorageState state, CancellationToken cancellationToken)
    {
        var storage = state.Storage;

        var parameters = new Dictionary<string, string>
        {
            ["type"] = "smb"
        };

        // Host is required for SMB
        if (!string.IsNullOrWhiteSpace(storage.Host))
        {
            parameters["host"] = storage.Host;
        }

        // Username for authentication
        if (!string.IsNullOrWhiteSpace(storage.Username))
        {
            parameters["user"] = storage.Username;
        }

        // Password for authentication (should be obscured by rclone)
        if (!string.IsNullOrWhiteSpace(storage.Password))
        {
            parameters["pass"] = storage.Password;
        }

        // Domain for Windows domain authentication
        if (!string.IsNullOrWhiteSpace(storage.Domain))
        {
            parameters["domain"] = storage.Domain;
        }

        // Port if non-default
        if (storage.Port.HasValue && storage.Port.Value > 0)
        {
            parameters["port"] = storage.Port.Value.ToString();
        }

        return Task.FromResult(parameters);
    }

    /// <inheritdoc />
    public override ValidationResult Validate(Storage storage)
    {
        var baseResult = base.Validate(storage);
        if (!baseResult.IsValid) return baseResult;

        // Host is required for SMB
        if (string.IsNullOrWhiteSpace(storage.Host))
        {
            return ValidationResult.Failure("Host is required for SMB storage");
        }

        return ValidationResult.Success();
    }
}
