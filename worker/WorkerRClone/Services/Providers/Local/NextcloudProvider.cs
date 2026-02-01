using Hannibal.Models;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers.Local;

/// <summary>
/// Storage provider for Nextcloud/ownCloud instances via WebDAV.
/// Uses app passwords for authentication (recommended over regular passwords).
/// </summary>
public class NextcloudProvider : CredentialStorageProviderBase
{
    public override string Technology => "nextcloud";

    public NextcloudProvider(ILogger<NextcloudProvider> logger) 
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
            ["type"] = "webdav",
            ["vendor"] = "nextcloud"
        };

        // Build the WebDAV URL for Nextcloud
        // Format: https://cloud.example.com/remote.php/dav/files/USERNAME/
        if (!string.IsNullOrWhiteSpace(storage.Host))
        {
            var host = storage.Host.Trim();
            var username = storage.Username?.Trim() ?? "";
            
            // Determine protocol - default to https
            string protocol = "https";
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = "http";
                host = host.Substring(7);
            }
            else if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                host = host.Substring(8);
            }
            
            // Remove trailing slash from host
            host = host.TrimEnd('/');
            
            // Add custom port if specified
            if (storage.Port.HasValue && storage.Port.Value > 0)
            {
                // Check if host already contains a port
                if (!host.Contains(':'))
                {
                    host = $"{host}:{storage.Port.Value}";
                }
            }
            
            // Build the WebDAV URL
            // Nextcloud WebDAV endpoint: /remote.php/dav/files/USERNAME/
            var webdavUrl = $"{protocol}://{host}/remote.php/dav/files/{username}/";
            parameters["url"] = webdavUrl;
        }

        // Username for authentication
        if (!string.IsNullOrWhiteSpace(storage.Username))
        {
            parameters["user"] = storage.Username.Trim();
        }

        // Password/App password for authentication - must be obscured in rclone's format
        var obscuredPassword = GetPasswordForRClone(storage);
        if (!string.IsNullOrEmpty(obscuredPassword))
        {
            parameters["pass"] = obscuredPassword;
        }

        return Task.FromResult(parameters);
    }

    /// <inheritdoc />
    public override ValidationResult Validate(Storage storage)
    {
        // Skip base validation since we have custom requirements
        if (string.IsNullOrWhiteSpace(storage.Technology))
            return ValidationResult.Failure("Technology is required");

        // Host is required for Nextcloud
        if (string.IsNullOrWhiteSpace(storage.Host))
        {
            return ValidationResult.Failure("Nextcloud server URL is required (e.g., cloud.example.com)");
        }

        // Username is required
        if (string.IsNullOrWhiteSpace(storage.Username))
        {
            return ValidationResult.Failure("Username is required for Nextcloud");
        }

        // Password/App password is required
        if (string.IsNullOrWhiteSpace(storage.Password))
        {
            return ValidationResult.Failure("Password or App Password is required for Nextcloud");
        }

        return ValidationResult.Success();
    }
}
