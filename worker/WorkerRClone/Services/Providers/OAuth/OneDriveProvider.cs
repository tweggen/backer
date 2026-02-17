using System.Net.Http.Headers;
using System.Text.Json;
using Hannibal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers.OAuth;

/// <summary>
/// Storage provider for Microsoft OneDrive
/// </summary>
public class OneDriveProvider : OAuthStorageProviderBase
{
    public override string Technology => "onedrive";

    public OneDriveProvider(
        ILogger<OneDriveProvider> logger,
        OAuth2ClientFactory oauth2ClientFactory,
        IServiceScopeFactory serviceScopeFactory)
        : base(logger, oauth2ClientFactory, serviceScopeFactory) 
    { 
    }

    /// <inheritdoc />
    public override async Task<Dictionary<string, string>> BuildRCloneParametersAsync(
        StorageState state, CancellationToken cancellationToken)
    {
        var storage = state.Storage;

        if (string.IsNullOrWhiteSpace(storage.AccessToken))
        {
            return new Dictionary<string, string>();
        }

        var (driveId, driveType) = await GetDriveInfoAsync(storage.AccessToken, cancellationToken);

        var parameters = new Dictionary<string, string>
        {
            ["type"] = "onedrive",
            ["client_id"] = storage.ClientId,
            ["client_secret"] = storage.ClientSecret,
            ["auth_url"] = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
            ["token_url"] = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            ["drive_id"] = driveId,
            ["drive_type"] = driveType,
            ["token"] = BuildTokenJson(storage)
        };

        return parameters;
    }

    /// <summary>
    /// Get the drive ID and type from Microsoft Graph API
    /// </summary>
    private async Task<(string DriveId, string DriveType)> GetDriveInfoAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri("https://graph.microsoft.com/")
        };
        
        client.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync(
            "v1.0/me/drive", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        
        var driveId = doc.RootElement.GetProperty("id").GetString() 
            ?? throw new InvalidOperationException("Drive ID not found in response");
        var driveType = doc.RootElement.GetProperty("driveType").GetString() 
            ?? throw new InvalidOperationException("Drive type not found in response");

        return (driveId, driveType);
    }
}
