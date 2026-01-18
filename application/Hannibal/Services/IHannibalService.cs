using Hannibal.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Endpoint = Hannibal.Models.Endpoint;

namespace Hannibal.Services;

public interface IHannibalService
{
    #region Users
    /*
     * Users
     */
    
    public Task<IdentityUser?> GetUserAsync(
        int id,
        CancellationToken cancellationToken);

    #endregion
    
    
    #region OAuth2
    
    public Task<TriggerOAuth2Result> TriggerOAuth2Async(
        OAuth2Params authParams,
        CancellationToken cancellationToken);

    public Task<ProcessOAuth2Result> ProcessOAuth2ResultAsync(
        HttpRequest httpRequest,
        string? code,
        string? state,
        string? error,
        string? errorDescription,
        CancellationToken cancellationToken);
    
    #endregion
    
    
    #region Endpoints
    /*
     * Endpoints
     */
    
    public Task<CreateEndpointResult> CreateEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken);

    public Task<Endpoint> GetEndpointAsync(
        string name,
        CancellationToken cancellationToken);

    public Task<IEnumerable<Endpoint>> GetEndpointsAsync(
        CancellationToken cancellationToken);
    
    public Task<Endpoint> UpdateEndpointAsync(
        int id,
        Endpoint endpoint,
        CancellationToken cancellationToken);

    public Task DeleteEndpointAsync(
        int id,
        CancellationToken cancellationToken);

    #endregion


    #region Storages
    /*
     * Storages
     */
    
    public Task<Storage> GetStorageAsync(
        int storageId,
        CancellationToken cancellationToken);

    public Task<IEnumerable<Storage>> GetStoragesAsync(
        CancellationToken cancellationToken);
    #endregion
    
    public Task<CreateStorageResult> CreateStorageAsync(
        Storage storage,
        CancellationToken cancellationToken);

    public Task DeleteStorageAsync(
        int storageId,
        CancellationToken cancellationToken);

    public Task<Storage> UpdateStorageAsync(
        int storageId,
        Storage storage,
        CancellationToken cancellationToken);

    #region Rules
    /*
     * Rules
     */

    public Task<CreateRuleResult> CreateRuleAsync(Rule rule, CancellationToken cancellationToken);

    public Task<Rule> GetRuleAsync(int ruleId, CancellationToken cancellationToken);

    public Task<IEnumerable<Rule>> GetRulesAsync(
        ResultPage resultPage, RuleFilter filter, CancellationToken cancellationToken);

    public Task<Rule> UpdateRuleAsync(
        int ruleId,
        Rule updatedRule,
        CancellationToken cancellationToken);

    public Task DeleteRuleAsync(
        int ruleId,
        CancellationToken cancellationToken);

    public Task FlushRulesAsync(
        CancellationToken cancellationToken);
    
    #endregion
    
    #region Jobs
    /*
     * Jobs
     */
    
    public Task<Job> GetJobAsync(int jobId, CancellationToken cancellationToken);

    public Task<IEnumerable<Job>> GetJobsAsync(ResultPage resultPage, JobFilter filter, CancellationToken cancellationToken);

    public Task<Job> AcquireNextJobAsync(AcquireParams acquireParams, CancellationToken cancellationToken);

    public Task DeleteJobsAsync(CancellationToken cancellationToken);
    
    /**
     * Report the current state of the job.
     *
     * Note, that if a job does not report its current state for a certain timeout span,
     * it is considered to be dead.
     */
    public Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken);
    
    #endregion
    
    #region Lifecycle
    /*
     * Lifecycle
     */
    
    public Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationToken);
    
    #endregion
    
    
    #region Config
    /*
     * Config
     */
    
    public Task<ConfigExport> ExportConfig(
        bool includeInactive, 
        CancellationToken cancellationToken);

    public Task<ImportResult> ImportConfig(
        string configJson,
        MergeStrategy mergeStrategy,
        CancellationToken cancellationToken);
    #endregion
}