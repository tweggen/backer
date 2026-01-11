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


    static public async Task<SortedDictionary<string, string>> _createOnedriveFromStorage(Storage storage)
    {
        // TXWTODO: Get drive ID here.
        #error Get ids of drive here
        return new()
        {
            { "type", "onedrive" },
            { "client_id", storage.ClientId },
            { "client_secret", storage.ClientSecret },
            { "token", _getRCloneToken(storage) }
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