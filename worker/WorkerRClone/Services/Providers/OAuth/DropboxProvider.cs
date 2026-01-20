using Hannibal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers.OAuth;

/// <summary>
/// Storage provider for Dropbox
/// </summary>
public class DropboxProvider : OAuthStorageProviderBase
{
    public override string Technology => "dropbox";

    public DropboxProvider(
        ILogger<DropboxProvider> logger,
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

        var parameters = new Dictionary<string, string>
        {
            ["type"] = "dropbox",
            ["client_id"] = storage.ClientId,
            ["client_secret"] = storage.ClientSecret,
            ["token"] = BuildTokenJson(storage)
        };

        return Task.FromResult(parameters);
    }
}
