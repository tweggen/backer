using Hannibal.Models;
using Hannibal.Services;

namespace Hannibal.Client;

public interface IHannibalServiceClient
{
    public Task<CreateRuleResult> CreateRuleAsync(Rule rule, CancellationToken cancellationToken);

    public Task<Rule> GetRuleAsync(int ruleId, CancellationToken cancellationToken);

    public Task<IEnumerable<Rule>> GetRulesAsync(
        ResultPage resultPage, RuleFilter filter, CancellationToken cancellationToken);

    public Task<Rule> UpdateRuleAsync(
        int id,
        Rule updatedRule,
        CancellationToken cancellationToken);

    public Task DeleteRuleAsync(
        int id,
        CancellationToken cancellationToken);
    
    public Task<Job> GetJobAsync(int jobId, CancellationToken cancellationToken);
    public Task<IEnumerable<Job>> GetJobsAsync(ResultPage resultPage, JobFilter filter, CancellationToken cancellationToken);
    public Task<Job> AcquireNextJobAsync(AcquireParams acquireParams, CancellationToken cancellationToken);
    public Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken);
    public Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationToken);
}