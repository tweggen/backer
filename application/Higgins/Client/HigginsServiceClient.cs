using System.Net.Http.Json;
using System.Text.Json;
using Higgins.Client.Configuration;
using Higgins.Models;
using Microsoft.Extensions.Options;

namespace Higgins.Client;

public class HigginsServiceClient : IHigginsServiceClient
{
    private readonly HttpClient _httpClient;

    public HigginsServiceClient(
        IOptions<HigginsServiceClientOptions> options,
        HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    
    
    public async Task<User> GetUserAsync(int id, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/users/{id}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<User>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    
    
    public async Task<CreateEndpointResult> CreateEndpointAsync(Endpoint endpoint, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/higgins/v1/endpoints", endpoint,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<CreateEndpointResult>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task<Endpoint> GetEndpointAsync(string name, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/endpoints/{Uri.EscapeDataString(name)}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<Endpoint>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    
    
    public async Task<IEnumerable<Endpoint>> GetEndpointsAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/endpoints",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<List<Endpoint>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public async Task<Storage> GetStorageAsync(int id, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/storages/{id}",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<Storage>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    
    
    public async Task<IEnumerable<Storage>> GetStoragesAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/storages",
            cancellationToken);
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(cancellationToken); 
        return JsonSerializer.Deserialize<List<Storage>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public async Task DeleteEndpointAsync(int id, CancellationToken cancellationToken)
    {
        var response = await _httpClient.DeleteAsync($"/api/higgins/v1/endpoints/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Endpoint> UpdateEndpointAsync(int id, Endpoint endpoint, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PutAsJsonAsync(
            $"/api/higgins/v1/endpoints/{id}", endpoint,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Endpoint>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}