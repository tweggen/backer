using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Models;

namespace Hannibal.Client;

public partial class HannibalServiceClient
{
    public async Task<CreateStorageResult> CreateStorageAsync(Storage storage, CancellationToken cancellationToken)
    {
        await SetAuthorizationHeader();

        var response = await _httpClient.PostAsJsonAsync(
            $"/api/hannibal/v1/storages", storage,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<CreateStorageResult>(
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

    
    public async Task<Storage> UpdateStorageAsync(int id, Storage storage, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PutAsJsonAsync(
            $"/api/hannibal/v1/storages/{id}", storage,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Storage>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    
    public async Task DeleteStorageAsync(int id, CancellationToken cancellationToken)
    {
        var response = await _httpClient.DeleteAsync($"/api/hannibal/v1/storages/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}