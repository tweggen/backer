﻿using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Client.Configuration;
using Hannibal.Models;
using Microsoft.Extensions.Options;

namespace Hannibal.Client;

public class HannibalServiceClient : IHannibalServiceClient
{
    private readonly HttpClient _httpClient;

    public HannibalServiceClient(
        IOptions<HannibalServiceClientOptions> options,
        HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    
    
    public Task<Job> GetJobAsync(int jobId)
    {
        throw new NotImplementedException();
    }

    
    public async Task<IEnumerable<Job>> GetJobsAsync(ResultPage resultPage, JobFilter filter)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/jobs?"
                +$"page={Uri.EscapeDataString((resultPage.Offset/resultPage.Length).ToString())}"
                +$"&minState={(int)filter.MinState}"
                +$"&maxState={(int)filter.MaxState}"
            );
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(); 
        return JsonSerializer.Deserialize<List<Job>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
   
    }

    
    public async Task<Job> AcquireNextJobAsync(AcquireParams acquireParams)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/hannibal/v1/acquireNextJob", acquireParams);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(); 
        return JsonSerializer.Deserialize<Job>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task<Result> ReportJobAsync(JobStatus jobStatus)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/hannibal/v1/reportJob",
            jobStatus);
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