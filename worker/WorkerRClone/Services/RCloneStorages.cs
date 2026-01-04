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


    static public SortedDictionary<string, string> _createDropboxFromStorage(Storage storage)
    {
        return new()
        {
            { "type", "dropbox" },
            { "token", storage.AccessToken }
        };
    }


    static public SortedDictionary<string, string> _createOnedriveFromStorage(Storage storage)
    {
        return new()
        {
            { "type", "onedrive" },
            { "token", storage.AccessToken }
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