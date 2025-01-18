using System.Net.Http.Json;

namespace WorkerRClone.Client;

public class RCloneClient
{
    private HttpClient _httpClient;

    public RCloneClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }


    public async Task<string> Sync(string uriFrom, string uriDest, CancellationToken cancellationToken)
    {
        /*
         * https://rclone.org/commands/rclone_sync/
         * srcFs - a remote name string e.g. "drive:src" for the source
         * dstFs - a remote name string e.g. "drive:dst" for the destination
         * createEmptySrcDirs - create empty src directories on destination if set
         */
        string srcFs = "";
        string destFs = "";
        var p = new
        {
            srcFs = srcFs,
            destFs = destFs
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/rc/sync", p, cancellationToken);
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