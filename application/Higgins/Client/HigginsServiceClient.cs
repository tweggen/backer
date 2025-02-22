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
    
    
    public async Task<User> GetUserAsync(int id)
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/users/{id}");
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(); 
        return JsonSerializer.Deserialize<User>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    
    
    public async Task<CreateEndpointResult> CreateEndpointAsync(Endpoint endpoint)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/higgins/v1/endpoints/create", endpoint);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<CreateEndpointResult>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task<Endpoint> GetEndpointAsync(string name)
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/endpoints/{Uri.EscapeDataString(name)}");
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(); 
        return JsonSerializer.Deserialize<Endpoint>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    
    
    public async Task<IEnumerable<Endpoint>> GetEndpointsAsync()
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/endpoints");
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(); 
        return JsonSerializer.Deserialize<List<Endpoint>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public async Task<Storage> GetStorageAsync(int id)
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/storages/{id}");
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(); 
        return JsonSerializer.Deserialize<Storage>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    
    
    public async Task<IEnumerable<Storage>> GetStoragesAsync()
    {
        var response = await _httpClient.GetAsync(
            $"/api/higgins/v1/storages");
        response.EnsureSuccessStatusCode(); 
        var content = await response.Content.ReadAsStringAsync(); 
        return JsonSerializer.Deserialize<List<Storage>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

}