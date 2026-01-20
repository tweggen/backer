using Hannibal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers.OAuth;

/// <summary>
/// Storage provider for Google Drive
/// </summary>
public class GoogleDriveProvider : OAuthStorageProviderBase
{
    public override string Technology => "googledrive";

    public GoogleDriveProvider(
        ILogger<GoogleDriveProvider> logger,
        OAuth2ClientFactory oauth2ClientFactory,
        IServiceScopeFactory serviceScopeFactory)
        : base(logger, oauth2ClientFactory, serviceScopeFactory) 
    { 
    }

    /// <inheritdoc />
    public override Task<Dictionary<string, string>> BuildRCloneParametersAsync(
        StorageState state, CancellationToken cancellationToken)
    {
        var storage = state.Storage;

        if (string.IsNullOrWhiteSpace(storage.AccessToken))
        {
            return Task.FromResult(new Dictionary<string, string>());
        }

        // rclone uses "drive" as the type for Google Drive
        var parameters = new Dictionary<string, string>
        {
            ["type"] = "drive",
            ["client_id"] = storage.ClientId,
            ["client_secret"] = storage.ClientSecret,
            ["scope"] = "drive",  // Full access; use "drive.file" for limited access
            ["token"] = BuildTokenJson(storage)
        };

        return Task.FromResult(parameters);
    }
}
