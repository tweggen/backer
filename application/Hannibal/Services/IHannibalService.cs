using Hannibal.Models;

namespace Hannibal.Services;

public interface IHannibalService
{
    public Task<User> GetUserAsync(
        int id,
        CancellationToken cancellationToken);

    public Task<CreateEndpointResult> CreateEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken);

    public Task<Endpoint> GetEndpointAsync(
        string name,
        CancellationToken cancellationToken);

    public Task<IEnumerable<Endpoint>> GetEndpointsAsync(
        CancellationToken cancellationToken);
    
    public Task<Storage> GetStorageAsync(
        int id,
        CancellationToken cancellationToken);

    public Task<IEnumerable<Storage>> GetStoragesAsync(
        CancellationToken cancellationToken);

    public Task DeleteEndpointAsync(
        int id,
        CancellationToken cancellationToken);

    public Task<Endpoint> UpdateEndpointAsync(
        int id,
        Endpoint endpoint,
        CancellationToken cancellationToken);

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
    
    /**
     * Report the current state of the job.
     *
     * Note, that if a job does not report its current state for a certain timeout span,
     * it is considered to be dead.
     */
    public Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken);
    public Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationToken);
    
    
    public Task<ConfigExport> ExportConfig(
        bool includeInactive, 
        CancellationToken cancellationToken);

    public Task<ImportResult> ImportConfig(
        string configJson,
        MergeStrategy mergeStrategy,
        CancellationToken cancellationToken);

}