using System.Net.Http.Headers;
using System.Text.Json;
using Hannibal;
using Hannibal.Client;
using Hannibal.Configuration;
using Hannibal.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkerRClone.Client;
using WorkerRClone.Configuration;
using WorkerRClone.Services;
using EndpointState = WorkerRClone.Services.EndpointState;

namespace WorkerRClone;

/**
 * Service to maintain a list of storages.
 */
public class RCloneStorages
{
    private OAuthOptions _oauthOptions;
    private readonly ILogger<RCloneStorages> _logger;
    private IServiceScopeFactory _serviceScopeFactory;

    private readonly OAuth2ClientFactory _oauth2ClientFactory;
    
    private SortedDictionary<string, StorageState> _mapStorageStates = new();
    
    public RCloneStorages(
        ILogger<RCloneStorages> logger,
        IOptionsMonitor<RCloneServiceOptions> optionsMonitor,
        IServiceScopeFactory serviceScopeFactory)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _oauthOptions = optionsMonitor.CurrentValue.OAuth2 ?? new OAuthOptions();
        _oauth2ClientFactory = new OAuth2ClientFactory(_oauthOptions);

        optionsMonitor.OnChange(async updated =>
        {
            _logger.LogInformation($"RCloneStorages: Options changed to {updated}.");
            if (updated?.OAuth2 != null)
            {
                _onUpdateOptions(updated.OAuth2);
            }
        });
    }


    private void _onUpdateOptions(OAuthOptions? oAuthOptions)
    {
        if (null == oAuthOptions) return;
        _oauthOptions = oAuthOptions;
        _oauth2ClientFactory.OnUpdateOptions(oAuthOptions);
    }
    
    
    public async Task _fillDropboxFromStorageAsync(
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
        
        ss.RCloneParameters = new()
        {
            { "type", "dropbox" },
            { "client_id", storage.ClientId },
            { "client_secret", storage.ClientSecret },
            { "token", tokenJson }
        };
        
        ss.OAuthClient = _oauth2ClientFactory.CreateOAuth2Client(
            new Guid(), "onedrive");
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
        if (null == client)
        {
            throw new InvalidOperationException("No http client available, should have been setup earlier.");
        }

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

    
    public async Task _fillOnedriveFromStorageAsync(
        WorkerRClone.Services.StorageState ss, CancellationToken cancellationToken)
    {
        var storage = ss.Storage;
        
        /*
         * We need an http client for a couple of operations.
         */
        ss.HttpClient = new HttpClient()
        {
            BaseAddress = new Uri("https://graph.microsoft.com/")
        };

        ss.OAuthClient = _oauth2ClientFactory.CreateOAuth2Client(
            new Guid(), "onedrive");
        
        /*
         * Make sure we have a current accesstoken.
         */
        var oldAccessToken = ss.Storage.AccessToken;
        var oldRefreshToken = ss.Storage.RefreshToken;
        
        var newAccessToken = await ss.OAuthClient.GetCurrentTokenAsync(
            ss.Storage.RefreshToken, false, cancellationToken);
        
        if (string.IsNullOrEmpty(newAccessToken))
        {
            throw new UnauthorizedAccessException("No access token found for onedrive.");
        }
        
        var newRefreshToken = ss.OAuthClient.RefreshToken;
        var newExpiresAt = ss.OAuthClient.ExpiresAt;

        bool tokensChanged = false;
        
        if (oldAccessToken != newAccessToken)
        {
            _logger.LogInformation($"Access token refreshed for storage {storage.UriSchema}");
            storage.AccessToken = newAccessToken;
            tokensChanged = true;
        }
        
        if (!string.IsNullOrEmpty(newRefreshToken) && oldRefreshToken != newRefreshToken)
        {
            _logger.LogInformation($"Refresh token updated for storage {storage.UriSchema}");
            storage.RefreshToken = newRefreshToken;
            tokensChanged = true;
        }
    
        if (newExpiresAt != default && storage.ExpiresAt != newExpiresAt)
        {
            storage.ExpiresAt = newExpiresAt;
            tokensChanged = true;
        }
        
        if (tokensChanged)
        {
            await _updateStorageInDatabase(storage, cancellationToken);
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

        ss.RCloneParameters = new()
        {
            { "type", "onedrive" },
            { "client_id", storage.ClientId },
            { "client_secret", storage.ClientSecret },
            { "drive_id", driveId }, { "drive_type", driveType },
            { "token", tokenJson }
        };

        ss.HttpClient.Dispose();
        ss.HttpClient = null;
    }
    
    
    private async Task _updateStorageInDatabase(Storage storage, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
        
            await hannibalService.UpdateStorageAsync(storage.Id, storage, cancellationToken);
        
            _logger.LogInformation($"Updated storage {storage.UriSchema} tokens in database");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to update storage in database: {ex}");
        }
    }
    

    /**
     * Fill the storagestate with everything that is specific to one
     * provider.
     */
    private async Task _fillProviderSpecific(
        WorkerRClone.Services.StorageState ss, CancellationToken cancellationToken)
    {
        var provider = ss.Storage.Technology;
        
        switch (provider)
        {
            case "dropbox":
                await _fillDropboxFromStorageAsync(ss, cancellationToken);
                break;
            
            case "onedrive":
                await _fillOnedriveFromStorageAsync(ss, cancellationToken);
                break;
            
            default:
                /*
                 * Not supported or no config required.
                 */
                ss.RCloneParameters = new();
                break;
        }

    }


    private async Task<StorageState> _createStorageState(Storage storage, CancellationToken cancellationToken)
    {
        StorageState ss = new()
        {
            Storage = storage,
        };

        /*
         * Now fill everything that is specific for one particular provider. 
         */
        try
        {
            await _fillProviderSpecific(ss, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError($"Error filling out provider specific for {storage.Technology}: {e}");
        }

        return ss;
    }

    
    public async Task<StorageState> FindStorageState(Storage storage, CancellationToken cancellationToken)
    {
        // TXWTODO: We know that we would require locking for the map. However, we create it at the very beginning.
        StorageState ss;
        if (_mapStorageStates.TryGetValue(storage.Technology, out ss))
        {
        }
        else
        {
            ss = await _createStorageState(storage, cancellationToken);
            _mapStorageStates[storage.Technology] = ss;
        }

        return ss;
    }
}