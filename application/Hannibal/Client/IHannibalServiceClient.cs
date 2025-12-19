using Hannibal.Models;
using Hannibal.Services;
using Microsoft.AspNetCore.Identity;

namespace Hannibal.Client;

public interface IHannibalServiceClient
{
    public IHannibalServiceClient SetAuthCookie(string authCookie);

    #region User
    /**
     * Return information about the given user.
     * @param id
     *     The id of the user to query. -1 means current user.
     *     Note that for standard users values other than -1 are not supported.
     */
    public Task<IdentityUser?> GetUserAsync(int id, CancellationToken cancellationToken);
    #endregion
    
    #region Endpoints
    public Task<CreateEndpointResult> CreateEndpointAsync(Endpoint endpoint, CancellationToken cancellationToken);
    public Task<Endpoint> GetEndpointAsync(string name, CancellationToken cancellationToken);
    public Task<IEnumerable<Endpoint>> GetEndpointsAsync(CancellationToken cancellationToken);
    public Task<Endpoint> UpdateEndpointAsync(int id, Endpoint endpoint, CancellationToken cancellationToken);
    public Task DeleteEndpointAsync(int id, CancellationToken cancellationToken);
    #endregion
    
    #region Storages
    public Task<CreateStorageResult> CreateStorageAsync(Storage storage, CancellationToken cancellationToken);
    public Task<Storage> GetStorageAsync(int id, CancellationToken cancellationToken);
    public Task<IEnumerable<Storage>> GetStoragesAsync(CancellationToken cancellationToken);
    public Task<Storage> UpdateStorageAsync(int id, Storage storage, CancellationToken cancellationToken);
    public Task DeleteStorageAsync(int id, CancellationToken cancellationToken);
    #endregion
    
    #region Rules
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
    #endregion
    
    #region Jobs
    public Task<Job> GetJobAsync(int jobId, CancellationToken cancellationToken);
    public Task<IEnumerable<Job>> GetJobsAsync(ResultPage resultPage, JobFilter filter, CancellationToken cancellationToken);
    public Task<Job?> AcquireNextJobAsync(AcquireParams acquireParams, CancellationToken cancellationToken);
    public Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken);
    public Task DeleteJobsAsync(CancellationToken cancellationToken);
    #endregion
    
    #region Lifecycle
    public Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationToken);
    #endregion
}