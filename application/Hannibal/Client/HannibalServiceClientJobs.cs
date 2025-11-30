using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Models;

namespace Hannibal.Client;

public partial class HannibalServiceClient
{
    public async Task<Job> GetJobAsync(int jobId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/jobs/{jobId}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<Job>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task<IEnumerable<Job>> GetJobsAsync(ResultPage resultPage, JobFilter filter, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/jobs?"
                +$"page={Uri.EscapeDataString((resultPage.Offset/resultPage.Length).ToString())}"
                +$"&minState={(int)filter.MinState}"
                +$"&maxState={(int)filter.MaxState}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<List<Job>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task<Job?> AcquireNextJobAsync(AcquireParams acquireParams, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/hannibal/v1/acquireNextJob", acquireParams,
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<Job>(
                content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        else
        {
            return null;
        }
    }

    
    public async Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/hannibal/v1/reportJob",
            jobStatus, cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<Result>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }


    public async Task DeleteJobsAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.DeleteAsync("/api/hannibal/v1/jobs", cancellationToken);
        response.EnsureSuccessStatusCode();
        return;
    }
}