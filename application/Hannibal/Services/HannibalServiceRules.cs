using Hannibal.Models;
using Hannibal.Services.Scheduling;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services;

public partial class HannibalService
{
    /**
     * Gate B (plan-git-repo-storage.md): decide which engine a rule's
     * endpoint pair would need and refuse every rule no engine can run.
     * Must run after both endpoints are loaded WITH their Storage - the
     * technology lives on Storage, not Endpoint. Self-mirror is checked
     * before the "engine not available"/"Sync not enabled" messages so a
     * git+git rule pointing at one repository twice gets the more specific
     * message even though both would otherwise throw.
     *
     * Gate D (this flip): worker/WorkerGit's mirror engine exists now, so
     * git+git is no longer rejected wholesale - Copy and Nop are allowed
     * (the engine only ever pushes additively at this gate, plan §5 "Copy
     * never forces and never deletes"). Sync stays rejected until Gate E
     * ships the safety guards (zero-ref/shrink/force-push/adopt, plan §5)
     * that make forcing and deleting on the destination safe.
     */
    private static void _validateRuleEndpoints(
        Endpoint sourceEndpoint, Endpoint destinationEndpoint, Rule.RuleOperation operation)
    {
        var engine = JobEngineClassifier.Classify(sourceEndpoint, destinationEndpoint);

        if (engine == JobEngine.Unsupported)
        {
            throw new ArgumentException(
                $"A rule cannot pair technology '{sourceEndpoint.Storage.Technology}' with " +
                $"'{destinationEndpoint.Storage.Technology}': no engine supports mixed transfers.");
        }

        if (engine == JobEngine.Git)
        {
            var sourceUrl = GitRemoteUrl.Normalize(sourceEndpoint.Storage.Host, sourceEndpoint.Path);
            var destinationUrl = GitRemoteUrl.Normalize(destinationEndpoint.Storage.Host, destinationEndpoint.Path);
            if (string.Equals(sourceUrl, destinationUrl, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Source and destination are the same repository; a rule cannot mirror a repository to itself.");
            }

            if (operation == Rule.RuleOperation.Sync)
            {
                throw new ArgumentException(
                    "Sync for git rules is not enabled until Gate E ships the safety guards named in " +
                    "plan-git-repo-storage.md §5 (zero-ref, shrink, force-push and adopt guards); use Copy until then.");
            }
        }
    }

    public async Task<CreateRuleResult> CreateRuleAsync(
        Rule rule,
        CancellationToken cancellationToken)
    {
        await _obtainUser();

        rule.UserId = _currentUser.Id;

        var sourceEndpoint = await _context.Endpoints.Include(e => e.Storage)
            .FirstAsync(e => e.Id == rule.SourceEndpointId, cancellationToken);
        if (null == sourceEndpoint)
        {
            throw new KeyNotFoundException($"No source endpoint found for endpointid {sourceEndpoint.Id}");
        }

        rule.SourceEndpoint = sourceEndpoint;
        rule.SourceEndpointId = sourceEndpoint.Id;

        var destinationEndpoint = await _context.Endpoints.Include(e => e.Storage)
            .FirstAsync(e => e.Id == rule.DestinationEndpointId, cancellationToken);
        if (null == destinationEndpoint)
        {
            throw new KeyNotFoundException($"No destination endpoint found for endpointid {destinationEndpoint.Id}");
        }

        rule.DestinationEndpoint = destinationEndpoint;
        rule.DestinationEndpointId = destinationEndpoint.Id;

        _validateRuleEndpoints(sourceEndpoint, destinationEndpoint, rule.Operation);

        await _context.Rules.AddAsync(rule, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        // Notify scheduler about new rule
        await _schedulerEventPublisher.PublishEventAsync(new RuleChangedEvent
        {
            RuleId = rule.Id,
            ChangeType = RuleChangeType.Created
        });

        return new CreateRuleResult() { Id = rule.Id };
    }
    

    public async Task<Rule> UpdateRuleAsync(
        int id,
        Rule updatedRule,
        CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        var rule = await _context.Rules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (rule == null)
        {
            throw new KeyNotFoundException($"No rule found for id {id}");
        }

        rule.UserId = _currentUser.Id;

        var sourceEndpoint = await _context.Endpoints.Include(e => e.Storage).FirstOrDefaultAsync(
            e => e.Id == updatedRule.SourceEndpointId, cancellationToken);
        if (null == sourceEndpoint)
        {
            throw new KeyNotFoundException($"No source endpoint found for endpointid {updatedRule.SourceEndpointId}");
        }

        var destinationEndpoint = await _context.Endpoints.Include(e => e.Storage).FirstOrDefaultAsync(
            e => e.Id == updatedRule.DestinationEndpointId, cancellationToken);
        if (null == destinationEndpoint)
        {
            throw new KeyNotFoundException($"No destination endpoint found for endpointid {updatedRule.DestinationEndpointId}");
        }

        _validateRuleEndpoints(sourceEndpoint, destinationEndpoint, updatedRule.Operation);

        // Check if scheduling-relevant fields changed BEFORE updating
        bool hasSchedulingChanges =
            rule.SourceEndpointId != updatedRule.SourceEndpointId ||
            rule.DestinationEndpointId != updatedRule.DestinationEndpointId ||
            rule.Operation != updatedRule.Operation ||
            rule.MaxDestinationAge != updatedRule.MaxDestinationAge ||
            rule.MinRetryTime != updatedRule.MinRetryTime ||
            rule.MaxTimeAfterSourceModification != updatedRule.MaxTimeAfterSourceModification ||
            rule.DailyTriggerTime != updatedRule.DailyTriggerTime;

        // Update all properties
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

        // Only notify scheduler if scheduling-relevant fields changed
        if (hasSchedulingChanges)
        {
            await _schedulerEventPublisher.PublishEventAsync(new RuleChangedEvent
            {
                RuleId = rule.Id,
                ChangeType = RuleChangeType.Updated
            });
        }

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

        // Notify scheduler about deleted rule
        await _schedulerEventPublisher.PublishEventAsync(new RuleChangedEvent
        {
            RuleId = id,
            ChangeType = RuleChangeType.Deleted
        });
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


    public async Task FlushRulesAsync(
        CancellationToken cancellationToken)
    {
        await _obtainUser(); // If you need user context
    
        var list = await _context.RuleStates
            .Where(rs => rs.Rule.UserId == _currentUser!.Id)
            .ToListAsync(cancellationToken);
    
        foreach (var rs in list)
        {
            rs.ExpiredAfter = DateTime.MinValue;
        }
    
        await _context.SaveChangesAsync(cancellationToken);
    
        await _hannibalHub.Clients.All.SendAsync("RulesFlushed");
    }


    public class RuleStateDto
    {
        public int RuleId { get; set; }
        public DateTime ExpiredAfter { get; set; }
        public int? RecentJobId { get; set; }
    }

    public async Task<IEnumerable<RuleStateDto>> GetRuleStatesAsync(CancellationToken cancellationToken)
    {
        await _obtainUser();
        var states = await _context.RuleStates
            .Where(rs => rs.Rule.UserId == _currentUser!.Id)
            .Select(rs => new RuleStateDto
            {
                RuleId = rs.RuleId,
                ExpiredAfter = rs.ExpiredAfter,
                RecentJobId = rs.RecentJob != null ? (int?)rs.RecentJob.Id : null
            })
            .ToListAsync(cancellationToken);
        return states;
    }
}