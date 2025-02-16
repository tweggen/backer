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
    
    
    public async Task<CreateEndpointResult> CreateEndpointAsync(Endpoint endpoint)
    {
        throw new NotImplementedException();
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

}