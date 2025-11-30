using Hannibal.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services;

public partial class HannibalService
{
        public async Task<CreateRuleResult> CreateRuleAsync(
        Rule rule,
        CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        rule.UserId = _currentUser.Id;

        var sourceEndpoint = await _context.Endpoints.FirstAsync(e => e.Id == rule.SourceEndpointId, cancellationToken);
        if (null == sourceEndpoint)
        {
            throw new KeyNotFoundException($"No source endpoint found for endpointid {sourceEndpoint.Id}");
        }
        rule.SourceEndpoint = sourceEndpoint;
        rule.SourceEndpointId = sourceEndpoint.Id;
        
        var destinationEndpoint =
            await _context.Endpoints.FirstAsync(e => e.Id == rule.DestinationEndpointId, cancellationToken);
        if (null == destinationEndpoint)
        {
            throw new KeyNotFoundException($"No destination endpoint found for endpointid {destinationEndpoint.Id}");
        }
        rule.DestinationEndpoint = destinationEndpoint;
        rule.DestinationEndpointId = destinationEndpoint.Id;
            
        await _context.Rules.AddAsync(rule, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new CreateRuleResult() { Id = rule.Id };
    }
    

    public async Task<Rule> UpdateRuleAsync(
        int id,
        Rule updatedRule,
        CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        var rule = await _context.Rules.FirstAsync(r => r.Id == id, cancellationToken);
                
        if (rule == null)
        {
            throw new KeyNotFoundException($"No rule found for id {id}");
        }

        rule.UserId = _currentUser.Id;

        var sourceEndpoint = await _context.Endpoints.FirstAsync(e => e.Id == rule.SourceEndpointId, cancellationToken);
        if (null == sourceEndpoint)
        {
            throw new KeyNotFoundException($"No source endpoint found for endpointid {sourceEndpoint.Id}");
        }
        rule.SourceEndpoint = sourceEndpoint;
        
        var destinationEndpoint =
            await _context.Endpoints.FirstAsync(e => e.Id == rule.DestinationEndpointId, cancellationToken);
        if (null == destinationEndpoint)
        {
            throw new KeyNotFoundException($"No destination endpoint found for endpointid {destinationEndpoint.Id}");
        }
        rule.DestinationEndpoint = destinationEndpoint;
            

        // Update other properties
        rule.Name = updatedRule.Name;
        rule.Comment = updatedRule.Comment;
        // We do not allow to change the username
        rule.SourceEndpoint = sourceEndpoint;
        rule.SourceEndpointId = sourceEndpoint.Id;
        rule.DestinationEndpoint = destinationEndpoint;
        rule.DestinationEndpointId = destinationEndpoint.Id;
        rule.Operation = updatedRule.Operation;
        rule.MaxDestinationAge = updatedRule.MaxDestinationAge;
        rule.MinRetryTime = updatedRule.MinRetryTime;
        rule.MaxTimeAfterSourceModification = updatedRule.MaxTimeAfterSourceModification;
        rule.DailyTriggerTime = updatedRule.DailyTriggerTime;

        await _context.SaveChangesAsync(cancellationToken);

        /*
         * There might be a new job available right now.
         */
        await _hannibalHub.Clients.All.SendAsync("NewJobAvailable");

        return rule;
    }
    
    
    public async Task DeleteRuleAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var rule = await _context.Rules.FirstAsync(e => e.Id == id, cancellationToken);
        if (rule == null)
        {
            throw new KeyNotFoundException($"No rule found for id {id}");
        }

        _context.Rules.Remove(rule);
        await _context.SaveChangesAsync(cancellationToken);
    }
    

    public async Task<Rule> GetRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Information requested about rule {ruleId}", ruleId);

        var rule = await _context.Rules.FindAsync(ruleId);
        if (null == rule)
        {
            throw new KeyNotFoundException($"No rule found with id {ruleId}.");
        }

        return rule;
    }


    public async Task<IEnumerable<Rule>> GetRulesAsync(
        ResultPage resultPage, RuleFilter filter, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Rule list requested");

        var list = await _context.Rules.ToListAsync(cancellationToken);
        return list;
    }
}