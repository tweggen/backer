using System.Net.Http.Headers;
using System.Net.Http.Json;
using WorkerRClone.Client.Models;

namespace WorkerRClone.Client;

public class RCloneClient
{
    private HttpClient _httpClient;

    public RCloneClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }


    public async Task<string> SyncAsync(string uriFrom, string uriDest, CancellationToken cancellationToken)
    {
        /*
         * https://rclone.org/commands/rclone_sync/
         * srcFs - a remote name string e.g. "drive:src" for the source
         * dstFs - a remote name string e.g. "drive:dst" for the destination
         * createEmptySrcDirs - create empty src directories on destination if set
         */
        SyncParams syncParams = new()
        {
            _async = true,
            srcFs = uriFrom,
            dstFs = uriDest
        };
        
        JsonContent content = JsonContent.Create(syncParams, typeof(SyncParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/sync/sync", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);            
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
    
    
    public async Task<string> CopyAsync(string uriFrom, string uriDest, CancellationToken cancellationToken)
    {
        /*
         * https://rclone.org/commands/rclone_copy/
         * srcFs - a remote name string e.g. "drive:src" for the source
         * dstFs - a remote name string e.g. "drive:dst" for the destination
         * createEmptySrcDirs - create empty src directories on destination if set
         */
        CopyParams copyParams = new()
        {
            _async = true,
            srcFs = uriFrom,
            dstFs = uriDest,
            createEmptySrcDirs = true
        };
        
        JsonContent content = JsonContent.Create(copyParams, typeof(CopyParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/sync/copy", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);            
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }

}