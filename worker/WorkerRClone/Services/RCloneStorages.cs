using System.Net.Http.Headers;
using System.Text.Json;
using Hannibal.Configuration;
using Microsoft.Extensions.Logging;
using WorkerRClone.Services;

namespace WorkerRClone;

public class RCloneStorages
{
    private OAuthOptions _oAuthOptions;
    private ILogger<RCloneService> _logger;

    
    public RCloneStorages(ILogger<RCloneService> logger, OAuthOptions oAuthOptions)
    {
        _logger = logger;
        _oAuthOptions = oAuthOptions;
    }


    public void OnUpdateOptions(OAuthOptions? oAuthOptions)
    {
        if (null == oAuthOptions) return;
        _oAuthOptions = oAuthOptions;
    }
    
    
    public async Task<SortedDictionary<string, string>> _createDropboxFromStorageAsync(
        WorkerRClone.Services.EndpointState es, CancellationToken cancellationToken)
    {
        var storage = es.Endpoint.Storage;
        
        /*
         * Generate a suitable dropbox token object.
         */
        var tokenObject = new
        {
            access_token = storage.AccessToken,
            refresh_token = storage.RefreshToken,
            token_type = "bearer", 
            expiry = storage.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        }; 
        string tokenJson = JsonSerializer.Serialize(tokenObject);
        
        return new()
        {
            { "type", "dropbox" },
            { "client_id", storage.ClientId },
            { "client_secret", storage.ClientSecret },
            { "token", tokenJson }
        };
    }


    /**
     * Return the drive id and drive type for a consumer onedrive.
     */
    private async Task<(string DriveId, string DriveType)> _getOneDriveInfoAsync(
        WorkerRClone.Services.EndpointState es, 
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = es.HttpClient;

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync(
            "https://graph.microsoft.com/v1.0/me/drive",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        string driveId = doc.RootElement.GetProperty("id").GetString()!;
        string driveType = doc.RootElement.GetProperty("driveType").GetString()!;

        return (driveId, driveType);
    }

    
    public async Task<SortedDictionary<string, string>> _createOnedriveFromStorageAsync(
        WorkerRClone.Services.EndpointState es, CancellationToken cancellationToken)
    {
        var storage = es.Endpoint.Storage;
        
        /*
         * Make sure we have a current accesstoken.
         */
        var oldAccessToken = es.Endpoint.Storage.AccessToken;
        var newAccessToken = await es.OAuthClient.GetCurrentTokenAsync();
        if (string.IsNullOrEmpty(newAccessToken))
        {
            throw new UnauthorizedAccessException("No access token found for onedrive.");
        }

        if (oldAccessToken != newAccessToken)
        {
            // TXWTODO: Update access token in the database.
            /*
             * Update access token for others.
             */
        }
        
        var (driveId, driveType) = await _getOneDriveInfoAsync(
            es, newAccessToken, cancellationToken);

        var tokenObject = new
        {
            access_token = newAccessToken,
            refresh_token = storage.RefreshToken,
            token_type = "bearer", 
            expiry = storage.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        }; 
        string tokenJson = JsonSerializer.Serialize(tokenObject);

        return new()
        {
            { "type", "onedrive" },
            { "client_id", storage.ClientId },
            { "client_secret", storage.ClientSecret },
            { "drive_id", driveId }, { "drive_type", driveType },
            { "token", tokenJson }
        };
    }


    public async Task<SortedDictionary<string, string>> CreateFromStorageAsync(
        WorkerRClone.Services.EndpointState es, CancellationToken cancellationToken)
    {
        var storage = es.Endpoint.Storage; 
        switch (storage.Technology)
        {
            case "dropbox":
                return await _createDropboxFromStorageAsync(es, cancellationToken);
            
            case "onedrive":
                return await _createOnedriveFromStorageAsync(es, cancellationToken);
            
            default:
                /*
                 * Not supported or no config required.
                 */
                return new();
                break;
        }
    }
}