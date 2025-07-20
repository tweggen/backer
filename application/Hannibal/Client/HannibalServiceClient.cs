using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Client.Configuration;
using Hannibal.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Hannibal.Client;

public class HannibalServiceClient : IHannibalServiceClient
{
    private readonly HttpClient _httpClient;
    
    public HannibalServiceClient(
        IOptions<HannibalServiceClientOptions> options,
        HttpClient httpClient
    )
    {
        _httpClient = httpClient;
    }

    public IHannibalServiceClient SetAuthCookie(string authCookie)
    {
        return this;
    }

    public async Task<IdentityUser> GetUserAsync(int id, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/users/{id}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<IdentityUser>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }


    public async Task<CreateEndpointResult> CreateEndpointAsync(Endpoint endpoint, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/hannibal/v1/endpoints", endpoint,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<CreateEndpointResult>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task<Endpoint> GetEndpointAsync(string name, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/endpoints/{Uri.EscapeDataString(name)}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<Endpoint>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    
    
    public async Task<IEnumerable<Endpoint>> GetEndpointsAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/endpoints",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<List<Endpoint>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public async Task<Storage> GetStorageAsync(int id, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/storages/{id}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<Storage>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    
    
    public async Task<IEnumerable<Storage>> GetStoragesAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/storages",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<List<Storage>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    

    public async Task DeleteEndpointAsync(int id, CancellationToken cancellationToken)
    {
        var response = await _httpClient.DeleteAsync($"/api/hannibal/v1/endpoints/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    
    public async Task<Endpoint> UpdateEndpointAsync(int id, Endpoint endpoint, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PutAsJsonAsync(
            $"/api/hannibal/v1/endpoints/{id}", endpoint,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Endpoint>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
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
    

    public async Task<Rule> UpdateRuleAsync(int id, Rule updatedRule, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PutAsJsonAsync(
            $"/api/hannibal/v1/rules/{id}", updatedRule,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Rule>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task DeleteRuleAsync(int id, CancellationToken cancellationToken)
    {
        var response = await _httpClient.DeleteAsync($"/api/hannibal/v1/rules/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
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