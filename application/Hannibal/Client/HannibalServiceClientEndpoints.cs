using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Models;

namespace Hannibal.Client;

public partial class HannibalServiceClient
{
    public async Task<CreateEndpointResult> CreateEndpointAsync(Endpoint endpoint, CancellationToken cancellationToken)
    {
        await SetAuthorizationHeader();

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


    public async Task DeleteEndpointAsync(int id, CancellationToken cancellationToken)
    {
        var response = await _httpClient.DeleteAsync($"/api/hannibal/v1/endpoints/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<RuleState>> GetRuleStatesAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/hannibal/v1/rule-states",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<RuleState>>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}