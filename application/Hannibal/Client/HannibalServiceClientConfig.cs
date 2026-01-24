using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Models;
using Hannibal.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace Hannibal.Client;

public partial class HannibalServiceClient
{
    public async Task<ConfigExport> ExportConfigAsync(
        bool includePasswords, 
        bool includeInactive, 
        CancellationToken cancellationToken)
    {
        await SetAuthorizationHeader();
        
        var queryParams = new Dictionary<string, string?>
        {
            ["includePasswords"] = includePasswords.ToString().ToLower(),
            ["includeInactive"] = includeInactive.ToString().ToLower()
        };
        
        var url = QueryHelpers.AddQueryString("/api/hannibal/v1/config/export", queryParams);
        
        var response = await _httpClient.GetAsync(url, cancellationToken);
        
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new ConfigExport();
            }

            return JsonSerializer.Deserialize<ConfigExport>(
                content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        
        throw new HttpRequestException($"Export failed with status {response.StatusCode}");
    }

    public async Task<ImportResult> ImportConfigAsync(
        string configJson, 
        MergeStrategy mergeStrategy, 
        CancellationToken cancellationToken)
    {
        await SetAuthorizationHeader();
        
        var request = new ConfigImportRequest
        {
            ConfigJson = configJson,
            MergeStrategy = mergeStrategy
        };
        
        var response = await _httpClient.PostAsJsonAsync(
            "/api/hannibal/v1/config/import", 
            request, 
            cancellationToken);
        
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new ImportResult { Errors = { "Empty response from server" } };
            }

            return JsonSerializer.Deserialize<ImportResult>(
                content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        
        return new ImportResult
        {
            Errors = { $"Import failed with status {response.StatusCode}" }
        };
    }
}
