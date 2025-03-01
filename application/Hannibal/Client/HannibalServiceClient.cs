using System.Net.Http.Json;
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


    public async Task<CreateRuleResult> CreateRuleAsync(Rule rule, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/hannibal/v1/rules", rule,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<CreateRuleResult>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task<Rule> GetRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/rules/{ruleId}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<Rule>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task<IEnumerable<Rule>> GetRulesAsync(ResultPage resultPage, RuleFilter filter, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/rules?"
            +$"page={Uri.EscapeDataString((resultPage.Offset/resultPage.Length).ToString())}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<List<Rule>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    

    public Task<Rule> UpdateRuleAsync(int id, Rule updatedRule, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    
    public Task DeleteRuleAsync(int id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    
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

    
    public async Task<Job> AcquireNextJobAsync(AcquireParams acquireParams, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/hannibal/v1/acquireNextJob", acquireParams,
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<Job>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
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

    
    public Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}