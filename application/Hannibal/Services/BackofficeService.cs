using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Hannibal.Services;

public class BackofficeService : BackgroundService
{
    private ILogger<BackofficeService> _logger;
    private IServiceScopeFactory _serviceScopeFactory;
    private HannibalServiceOptions _options;
    private readonly IHubContext<HannibalHub> _hannibalHub;

    
    public BackofficeService(
        ILogger<BackofficeService> logger,
        IServiceScopeFactory serviceScopeFactory,
        IOptions<HannibalServiceOptions> options,
        IHubContext<HannibalHub> hannibalHub)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _options = options.Value;
        _hannibalHub = hannibalHub;
    }


    /**
     * Compute all available jobs from the list of rules.
     *
     * TXWTODO: How do I know that all jobs already had been created?
     */
    private async Task _rules2Jobs(HannibalContext context, CancellationToken cancellationToken)
    {
        _logger.LogWarning("_rules2Jobs called.");
        
        /*
         * This implementation evaluates per one user.
         */
        DateTime now = DateTime.Now;

        List<Rule> myRules = await context
            .Rules
            // TXWTODO: .Where(r => r.User == me)
            .ToListAsync(cancellationToken);
        
        HashSet<Rule> setRulesToEval = new();
        
        List<RuleState> myRuleStates = await context
            .RuleStates
            // TXWTODO: .Where(r => r.User == me)
            .ToListAsync(cancellationToken);
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
            bool shallCompute = false;

            if (!dictRuleStates.TryGetValue(r, out rs))
            {
                rs = new()
                {
                    Rule = r,
                    ExpiredAfter = DateTime.MinValue,
                };
                isNewState = true;
                shallCompute = true;
            }
            else
            {
                switch (rs.RecentJob.State)
                {
                    case Job.JobState.Ready:
                        break;
                    case Job.JobState.DoneFailure:
                        _logger.LogWarning($"job last reported {rs.RecentJob.LastReported} min retry time {r.MinRetryTime}, now {now}", rs.RecentJob.Id);
                        if (rs.RecentJob.LastReported + r.MinRetryTime <= now)
                        {
                            shallCompute = true;
                        }

                        break;
                    case Job.JobState.DoneSuccess:
                        if (rs.ExpiredAfter <= now)
                        {
                            shallCompute = true;
                        }

                        break;
                    case Job.JobState.Executing:
                        break;
                    case Job.JobState.Preparing:
                        break;
                }
            }


            if (shallCompute)
            {
                /*
                * OK, we need a new job, Can we do it in one step or do we need an
                * intermediate copy?
                * 
                * TXWTODO: Ask the source endpoint if it is source only.
                */
                
                Job job = new()
                {
                    Tag = r.Name,
                    UserId = r.UserId,
                    FromRule = r,
                    Operation = r.Operation,
                    Owner = "",
                    State = Job.JobState.Ready,
                    // TXWTODO: Might use preferred starting time
                    StartFrom = now,
                    EndBy = now + new TimeSpan(24, 0,  0),
                    SourceEndpoint = r.SourceEndpoint,
                    DestinationEndpoint = r.DestinationEndpoint,
                    Status = 0
                };
                await context.Jobs.AddAsync(job, cancellationToken);
                rs.ExpiredAfter = now + r.MaxDestinationAge;
                rs.RecentJob = job;
            }

            if (isNewState)
            {
                await context.RuleStates.AddAsync(rs, cancellationToken);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        
        /*
         * There might be a new job available right now.
         */
        await _hannibalHub.Clients.All.SendAsync("NewJobAvailable");
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