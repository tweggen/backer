using System.Net.Http.Headers;
using System.Text.Json;
using Hannibal.Models;

namespace WorkerRClone;

public static class RCloneStorages
{
    static string _getRCloneToken(Storage storage)
    {
        var tokenObject = new
        {
            access_token = storage.AccessToken,
            refresh_token = storage.RefreshToken,
            token_type = "bearer", 
            expiry = storage.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        }; 
        string tokenJson = JsonSerializer.Serialize(tokenObject);
        return tokenJson;
    }
    

    static public async Task<SortedDictionary<string, string>> _createDropboxFromStorage(Storage storage)
    {
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


    private static async Task<(string DriveId, string DriveType)> _getOneDriveInfoAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
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

    
    static public async Task<SortedDictionary<string, string>> _createOnedriveFromStorage(
        Storage storage, CancellationToken cancellationToken = default)
    {
        var accessToken  = _getRCloneToken(storage);
        var (driveId, driveType) = await _getOneDriveInfoAsync(storage.AccessToken, cancellationToken);
        return new()
        {
            { "type", "onedrive" },
            { "client_id", storage.ClientId },
            { "client_secret", storage.ClientSecret },
            { "drive_id", driveId }, { "drive_type", driveType },
            { "token", accessToken }
        };
    }


    static public async Task<SortedDictionary<string, string>> CreateFromStorage(Storage storage)
    {
        switch (storage.Technology)
        {
            case "dropbox":
                return await _createDropboxFromStorage(storage);
            
            case "onedrive":
                return await _createOnedriveFromStorage(storage);
            
            default:
                /*
                 * Not supported or no config required.
                 */
                return new();
                break;
        }
    }
}