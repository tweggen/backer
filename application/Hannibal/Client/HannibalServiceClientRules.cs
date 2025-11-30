using System.Net.Http.Json;
using System.Text.Json;
using Hannibal.Models;

namespace Hannibal.Client;

public partial class HannibalServiceClient
{ 
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
}