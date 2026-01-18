using System.Net.Http.Headers;
using System.Text.Json;
using Hannibal;
using Hannibal.Configuration;
using Hannibal.Models;
using Microsoft.Extensions.Logging;
using WorkerRClone.Client;
using WorkerRClone.Services;
using EndpointState = WorkerRClone.Services.EndpointState;

namespace WorkerRClone;

public class RCloneStorages
{
    private OAuthOptions _oAuthOptions;
    private ILogger<RCloneService> _logger;
    private OAuth2ClientFactory _oauth2ClientFactory;

    
    public RCloneStorages(ILogger<RCloneService> logger, OAuthOptions oAuthOptions)
    {
        _logger = logger;
        _oAuthOptions = oAuthOptions;
        _oauth2ClientFactory = new OAuth2ClientFactory(oAuthOptions);
    }


    public void OnUpdateOptions(OAuthOptions? oAuthOptions)
    {
        if (null == oAuthOptions) return;
        _oAuthOptions = oAuthOptions;
        _oauth2ClientFactory.OnUpdateOptions(oAuthOptions);
    }
    
    
    public async Task<SortedDictionary<string, string>> _createDropboxFromStorageAsync(
        StorageState ss, CancellationToken cancellationToken)
    {
        var storage = ss.Storage;
        
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
        WorkerRClone.Services.StorageState ss, 
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = ss.HttpClient;

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
        WorkerRClone.Services.StorageState ss, CancellationToken cancellationToken)
    {
        var storage = ss.Storage;
        
        /*
         * Make sure we have a current accesstoken.
         */
        var oldAccessToken = ss.Storage.AccessToken;
        var newAccessToken = await ss.OAuthClient.GetCurrentTokenAsync();
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
            ss, newAccessToken, cancellationToken);

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


    public async Task<StorageState> CreateStorageStateAsync(
        Storage storage,
        HttpClient httpClient, RCloneClient rcloneClient,
        CancellationToken cancellationToken)
    {
        StorageState ss = new()
        {
            Storage = storage,
            HttpClient = httpClient,
            RCloneClient = rcloneClient
        };
        
        /*
         * Guid only is required for kkce which we currently do not support.
         */
        ss.OAuthClient = _oauth2ClientFactory.CreateOAuth2Client(
            new Guid(), 
            storage.UriSchema);

        return ss;
    }
    

    public async Task<SortedDictionary<string, string>> CreateFromStorageAsync(
        WorkerRClone.Services.StorageState ss, CancellationToken cancellationToken)
    {
        var storage = ss.Storage; 
        switch (storage.Technology)
        {
            case "dropbox":
                return await _createDropboxFromStorageAsync(ss, cancellationToken);
            
            case "onedrive":
                return await _createOnedriveFromStorageAsync(ss, cancellationToken);
            
            default:
                /*
                 * Not supported or no config required.
                 */
                return new();
                break;
        }
    }
}