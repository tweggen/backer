using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Configuration;
using Hannibal.Models;
using Microsoft.Extensions.Options;

namespace Hannibal.Client;

public class HannibalServiceClient : IHannibalServiceClient
{
    private readonly HttpClient _httpClient;

    public HannibalServiceClient(
        IOptions<HannibalServiceOptions> options,
        HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    
    
    public Task<Job> GetJobAsync(int jobId)
    {
        throw new NotImplementedException();
    }

    
    public Task<IEnumerable<Job>> GetJobsAsync(ResultPage resultPage, JobFilter filter)
    {
        throw new NotImplementedException();
    }

    
    public async Task<Job> AcquireNextJobAsync(string capabilities, string owner)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/hannibal/v1/acquireNextJob",
            new { capabilities = capabilities, owner = owner }
            );
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(); 
        return JsonSerializer.Deserialize<Job>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task<Result> ReportJobAsync(JobStatus jobStatus)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/hannibal/v1/reportJob",
            new { jobStatus = jobStatus }
        );
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(); 
        return JsonSerializer.Deserialize<Result>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public Task<ShutdownResult> ShutdownAsync()
    {
        throw new NotImplementedException();
    }
}