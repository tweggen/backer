using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Hannibal.Services;

public class BackofficeService : BackgroundService
{
    private ILogger<BackofficeService> _logger;
    private IServiceScopeFactory _serviceScopeFactory;
    private HannibalServiceOptions _options;
    
    
    public BackofficeService(
        ILogger<BackofficeService> logger,
        IServiceScopeFactory serviceScopeFactory,
        IOptions<HannibalServiceOptions> options)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _options = options.Value;
    }


    private async Task _rules2Jobs(HannibalContext context, CancellationToken cancellationToken)
    {
        /*
         * This implementation evaluates per one user.
         */
        DateTime now = DateTime.Now;

        List<Rule> myRules = await context
            .Rules
            // TXWTODO: .Where(r => r.User == me)
            .ToListAsync();
        
        HashSet<Rule> setRulesToEval = new();
        
        List<RuleState> myRuleStates = await context
            .RuleStates
            // TXWTODO: .Where(r => r.User == me)
            .ToListAsync();
        Dictionary<Rule, RuleState> dictRuleStates = new();

        foreach (var rs in myRuleStates)
        {
            if (rs.Rule != null)
            {
                dictRuleStates.Add(rs.Rule, rs);
            }
            else
            {
                // This is an invalid rule state, delete it.
            }
        }
        
        /*
         * Now process every rule. 
         */
        foreach (var r in myRules)
        {
            RuleState? rs = null;
            bool isNewState = false;
            if (!dictRuleStates.TryGetValue(r, out rs))
            {
                rs = new()
                {
                    Rule = r,
                    ExpiredAfter = DateTime.MinValue,
                };
                isNewState = true;
            }

            bool shallCompute = rs.ExpiredAfter < now;

            if (shallCompute)
            {
                Job job = new()
                {
                    Tag = r.Name,
                    FromRule = r,
                    Owner = "",
                    State = Job.JobState.Ready,
                    // TXWTODO: Might use preferred starting time
                    StartFrom = now,
                    EndBy = now + new TimeSpan(24, 0,  0),
                    SourceEndpoint = r.SourceEndpoint,
                    DestinationEndpoint = r.DestinationEndpoint,
                    Status = 0
                };
                await context.Jobs.AddAsync(job);
                rs.ExpiredAfter = now + r.MaxDestinationAge;
            }

            if (isNewState)
            {
                await context.RuleStates.AddAsync(rs);
            }
        }

        await context.SaveChangesAsync();
        
        // TXWTODO: Use SignalR to inform about new jobs.
    }
    
    
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            
            var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();
            
            await _rules2Jobs(context, cancellationToken);
            await Task.Delay(10_000, cancellationToken);
        }
    }
}