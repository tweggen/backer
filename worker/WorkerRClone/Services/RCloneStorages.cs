using System.Text.Json;
using Hannibal.Models;

namespace WorkerRClone;

public class RCloneStorages
{
    private SortedDictionary<string, SortedDictionary<string, string>> _mapRCloneConfigs = new();

    private string _strRCloneStorageConfig = "";


    public void AddConfig(string technology, SortedDictionary<string, string> config)
    {
        if (!_mapRCloneConfigs.ContainsKey(technology))
        {
            _mapRCloneConfigs.Add(technology, config);
        }
        else
        {
            // ignore silently.
        }
    }
    
    
    public bool HasStorage(string name)
    {
        return false;
    }


    public SortedDictionary<string, string> GetRCloneStorageConfig(string technology)
    {
        return _mapRCloneConfigs[technology];
    }

    
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
    

    static public SortedDictionary<string, string> _createDropboxFromStorage(Storage storage)
    {
        var tokenObject = new
        {
            access_token = storage.AccessToken,
            refresh_token = storage.RefreshToken,
            token_type = "bearer", 
            expiry = "0001-01-01T00:00:00Z"
        }; 
        string tokenJson = JsonSerializer.Serialize(tokenObject);

        return new()
        {
            { "type", "dropbox" },
            { "client_id", storage.ClientId },
            { "token", tokenJson }
        };
    }


    static public SortedDictionary<string, string> _createOnedriveFromStorage(Storage storage)
    {
        return new()
        {
            { "type", "onedrive" },
            { "client_id", storage.ClientId },
            { "token", _getRCloneToken(storage) }
        };
    }


    static public SortedDictionary<string, string> CreateFromStorage(Storage storage)
    {
        switch (storage.Technology)
        {
            case "dropbox":
                return _createDropboxFromStorage(storage);
            
            case "onedrive":
                return _createOnedriveFromStorage(storage);
            
            default:
                /*
                 * Not supported or no config required.
                 */
                return new();
                break;
        }
    }
    
    
    static public RCloneStorages CreateFromStorages(IEnumerable<Storage> storages)
    {
         var rcs = new RCloneStorages();
         
         foreach (var sto in storages)
         {
             try
             {
                 var config = CreateFromStorage(sto);
                 rcs.AddConfig(sto.Technology, config);
             }
             catch (Exception e)
             {
                 /*
                  * Unable to add config for one storage.
                  */
             }
         }

         return rcs;
    }
}