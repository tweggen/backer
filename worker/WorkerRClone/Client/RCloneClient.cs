using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WorkerRClone.Client.Models;

namespace WorkerRClone.Client;

public class RCloneClient
{
    private HttpClient _httpClient;

    public RCloneClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }


    public async Task Quit(CancellationToken cancellationToken, int exitCode = 0)
    {
        JobQuitParams jobQuitParamsParams = new()
        {
            exitCode = exitCode 
        };
        
        JsonContent content = JsonContent.Create(jobQuitParamsParams, typeof(JobQuitParams), new MediaTypeHeaderValue("application/json"));

        var response = await _httpClient.PostAsync("/core/quit", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }


    public async Task<JobPathsResult> GetPathsAsync(CancellationToken cancellationToken)
    {
        JobPathsParams jobPathsParams = new();
        
        JsonContent content = JsonContent.Create(jobPathsParams, typeof(JobPathsParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/config/paths", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            string responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<JobPathsResult>(
                responseString, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }


    public async Task<JobListResult> GetJobListAsync(CancellationToken cancellationToken)
    {
        JobListParams jobListParams = new();
        
        JsonContent content = JsonContent.Create(jobListParams, typeof(JobListParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/job/list", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            string responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<JobListResult>(
                responseString, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }


    public async Task StopJobAsync(CancellationToken cancellationToken)
    {
        StopJobParams stopJobParams = new();
        
        JsonContent content = JsonContent.Create(stopJobParams, typeof(StopJobParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/job/stop", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
    

    public async Task<JobStatsResult> GetJobStatsAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync("/core/stats", null, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            string responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<JobStatsResult>(
                responseString, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
    

    public async Task<ListRemotesResult> ListRemotesAsync(CancellationToken cancellationToken)
    {
        ListRemotesParams listRemotesParams = new();
        
        JsonContent content = JsonContent.Create(listRemotesParams, typeof(ListRemotesParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/config/listremotes", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            string responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<ListRemotesResult>(
                responseString, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
    

    public async Task<JobStatusResult> GetJobStatusAsync(int jobId, CancellationToken cancellationToken)
    {
        JobStatusParams jobStatusParamsParams = new()
        {
            jobid = jobId
        };
        
        JsonContent content = JsonContent.Create(jobStatusParamsParams, typeof(JobStatusParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/job/status", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            string responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<JobStatusResult>(
                responseString, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }


    public async Task<AsyncResult> NoopAsync(CancellationToken cancellationToken)
    {
        NoopParams noopParams = new()
        {
            _async = true,
        };

        JsonContent content = JsonContent.Create(noopParams, typeof(NoopParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/rc/noop", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            string responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<AsyncResult>(
                responseString, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
    
    
    public async Task<AsyncResult> SyncAsync(string uriFrom, string uriDest, CancellationToken cancellationToken)
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
            dstFs = uriDest,
            _config = new Config()
            {
                Exclude = new() { "_backer" } 
            }
        };
        
        JsonContent content = JsonContent.Create(syncParams, typeof(SyncParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/sync/sync", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            string responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<AsyncResult>(
                responseString, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
    
    
    public async Task<AsyncResult> CopyAsync(string uriFrom, string uriDest, CancellationToken cancellationToken)
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
            createEmptySrcDirs = true,
            _config = new Config()
            {
                Exclude = new() { "_backer" },
                CheckSum = true
            }
        };
        
        JsonContent content = JsonContent.Create(copyParams, typeof(CopyParams), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync("/sync/copy", content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            string responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<AsyncResult>(
                responseString, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }

}