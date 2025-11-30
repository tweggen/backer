using Hannibal.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services;

public partial class HannibalService
{
        public async Task<Job> GetJobAsync(int jobId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Information requested about job {jobId}", jobId);

        var job = await _context.Jobs.FindAsync(jobId);
        if (null == job)
        {
            throw new KeyNotFoundException($"No job found with id {jobId}.");
        }

        return job;
    }


    public async Task<IEnumerable<Job>> GetJobsAsync(
        ResultPage resultPage, JobFilter filter, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Job list requested");

        var list = await _context.Jobs.ToListAsync(cancellationToken);
        return list;
    }


    
    public async Task DeleteJobsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Delete jobs requested.");
        await _obtainUser();

        var jobsToDelete = await _context.Jobs
            .Where(j => j.UserId == _currentUser!.Id)
            .ToListAsync(cancellationToken);
        _context.Jobs.RemoveRange(jobsToDelete);

        await _context.SaveChangesAsync(cancellationToken);
    }

    
    private string _endpointKey(Endpoint endpoint) => $"{endpoint.StorageId}:{endpoint.Path}";
    

    /**
     * Acquire the next job to do.
     * We remember
     * - the owner of this job
     * - that the source endpoint of choice is reading
     * - that the target endpoint of choice is writing
     *
     * We take care that
     * - nobody is reading from the target endpoint or their parent
     * - nobody is writing the source endpoint or any endpoints within.
     */
    public async Task<Job> AcquireNextJobAsync(AcquireParams acquireParams, CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        // var user = await _context.Users.FirstAsync(u => u.Username == acquireParams.Username, cancellationToken); 
        _logger.LogInformation("new job requested by for client with capas {capabilities}", acquireParams.Capabilities);

        /*
         * convert the capabilities into a set of remotes.
         */
        var setRemotes = new HashSet<string>();
        var capaList = acquireParams.Capabilities.Split(',');
        foreach (var capa in capaList)
        {
            try
            {
                setRemotes.Add(capa.Trim());
            }
            catch (Exception e)
            {
                _logger.LogError($"Invalid capability {capa}, ifnoring: {e}");
            }
        }
        
        var listPossibleJobs = await _context.Jobs
            .Where(j => j.State == Job.JobState.Ready && j.Owner == "")
            .Include(j => j.SourceEndpoint)
            .Include(j => j.DestinationEndpoint)
            .OrderBy(j => j.StartFrom)
            .ToListAsync(cancellationToken);

        Job? job = null;
        var mapStates = await _gatherEndpointAccess(_currentUser.Id);
        string acquireNetworks = "";
        if (acquireParams.Networks != null)
        {
            acquireNetworks = acquireParams.Networks.Trim();
        }
        foreach (var candidate in listPossibleJobs)
        {
            if (!setRemotes.Contains(candidate.SourceEndpoint.Storage.UriSchema))
            {
                _logger.LogInformation($"Skipping job {candidate.Id} because source endpoint {candidate.SourceEndpoint.Name} is not in set of remotes.");
                continue;
            }

            if (!setRemotes.Contains(candidate.DestinationEndpoint.Storage.UriSchema))
            {
                _logger.LogInformation($"Skipping job {candidate.Id} because destination endpoint {candidate.DestinationEndpoint.Name} is not in set of remotes.");
                continue;
            }
            
            if (!String.IsNullOrWhiteSpace(candidate.SourceEndpoint.Storage.Networks)
                && acquireNetworks != candidate.SourceEndpoint.Storage.Networks.Trim())
            {
                _logger.LogInformation($"Skipping job {candidate.Id} because source storage is not in network");
                _logger.LogDebug($"{acquireNetworks} != {candidate.SourceEndpoint.Storage.Networks.Trim()}");
                continue;
            }

            if (!String.IsNullOrWhiteSpace(candidate.DestinationEndpoint.Storage.Networks)
                && acquireNetworks != candidate.DestinationEndpoint.Storage.Networks.Trim())
            {
                _logger.LogInformation($"Skipping job {candidate.Id} because destination storage is not in network");
                _logger.LogDebug($"{acquireNetworks} != {candidate.DestinationEndpoint.Storage.Networks.Trim()}");
                continue;
            }
            
            if (!_mayUseSourceEndpoint(candidate.SourceEndpoint, mapStates))
            {
                _logger.LogInformation($"Skipping job {candidate.Id} because source endpoint {candidate.SourceEndpoint.Name} is already in use.");
                continue;
            }
            
            if (!_mayUseDestinationEndpoint(candidate.DestinationEndpoint, mapStates))
            {
                _logger.LogInformation($"Skipping job {candidate.Id} because destination endpoint {candidate.DestinationEndpoint.Name} is already in use.");
                continue;
            }
            
            job = candidate;
        }
        
        if (job != null)
        {
            _logger.LogInformation("owner {owner} acquired job {jobId}.", acquireParams.Owner, job.Id);
            job.Owner = acquireParams.Owner;
            job.State = Job.JobState.Executing;
            job.LastReported = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return job;
        }
        else
        {
            throw new KeyNotFoundException(
                $"No job found for owner {acquireParams.Owner} with caps {acquireParams.Capabilities}");
        }
    }


    /**
     * Look, how many jobs of one particular user access the given andpoint
     * either reading or writing from a given network.
     *
     * This is assuming any given user would not have an excessive number of jobs running.
     */
    public async Task<SortedDictionary<string, EndpointState.AccessState>> 
        _gatherEndpointAccess(string userId)
    {
        List<Job> listTimedOut = new();
        SortedDictionary<string, EndpointState.AccessState> mapStates = new();

        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(120);

        var myOngoingJobs = await _context.Jobs
            .Where(j =>(j.UserId == userId)&& (j.State == Job.JobState.Executing))
            .Include(j => j.SourceEndpoint)
            .Include(j => j.DestinationEndpoint)
            .ToListAsync();
        foreach (var job in myOngoingJobs)
        {
            var age = now - job.LastReported;
            if (age > timeout)
            {
                _logger.LogInformation($"Timing out job {job.Tag}");
                listTimedOut.Add(job);
            }
            else
            {
                string jobSourceEndpointKey = _endpointKey(job.SourceEndpoint);
                if (mapStates.TryGetValue(jobSourceEndpointKey, out var sourceState))
                {
                    if (sourceState == EndpointState.AccessState.Writing)
                    {
                        _logger.LogWarning(
                            $"Warning: Endpoint {jobSourceEndpointKey} is in use for both reading and writing.");
                    }
                    else
                    {
                        /*
                         * In use for reading twice or idle.
                         */
                        mapStates[jobSourceEndpointKey] = EndpointState.AccessState.Reading;
                    }
                }
                else
                {
                    mapStates.Add(jobSourceEndpointKey, EndpointState.AccessState.Reading);
                }
            

                string jobDestinationEndpointKey = _endpointKey(job.DestinationEndpoint);
                if (mapStates.TryGetValue(jobDestinationEndpointKey, out var destState))
                {
                    if (destState != EndpointState.AccessState.Idle)
                    {
                        _logger.LogWarning($"Warning: Endpoint {jobDestinationEndpointKey} is in use for both {destState.ToString()}.");
                    }
                    else
                    {
                        /*
                         * That's ok.
                         */
                        mapStates[jobDestinationEndpointKey] = EndpointState.AccessState.Writing;
                    }
                }
                else
                {
                    mapStates.Add(jobDestinationEndpointKey, EndpointState.AccessState.Writing);
                }
            }
        }

        if (listTimedOut.Count > 0)
        {
            foreach (var job in listTimedOut)
            {
                job.State = Job.JobState.DoneFailure;
            }
            await _context.SaveChangesAsync();
        }

        return mapStates;
    }


    
    /**
     * Check, if the given endpoint may be used as a source.
     *
     * Any number of jobs may use the endpoint for reading.
     * However, it must not be in use for writing.
     */
    private bool _mayUseSourceEndpoint(
        Endpoint endpoint,
        SortedDictionary<string, EndpointState.AccessState> mapStates)
    {
        string endpointKey = _endpointKey(endpoint);
        foreach (var kvp in mapStates)
        {
            bool isWriting = kvp.Value == EndpointState.AccessState.Writing;
            if (isWriting)
            {
                bool isShared = endpointKey.StartsWith(kvp.Key) || kvp.Key.StartsWith(endpointKey);
                if (isShared)
                {
                    _logger.LogInformation($"Cannot use source endpoint {endpointKey} because it already is in use.");
                    return false;
                }
            }
        }

        return true;
    }
    
    
    /**
     * Check, if the given endpoint may be used as a destination.
     * Destination may only have one user.
     *
     * No other job must use it as a destination for writing ot
     * got reading.
     */
    private bool _mayUseDestinationEndpoint(
        Endpoint endpoint,
        SortedDictionary<string, EndpointState.AccessState> mapStates)
    {
        string endpointKey = _endpointKey(endpoint);
        foreach (var kvp in mapStates)
        {
            bool isShared = endpointKey.StartsWith(kvp.Key) || kvp.Key.StartsWith(endpointKey);
            if (isShared)
            {
                _logger.LogInformation($"Cannot use destination endpoint {endpointKey} because it already is in use.");
                return false;
            }
        }

        return true;
    }
    
    
    public async Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken)
    {
        _logger.LogInformation("job {jobId} reported back status {jobStatus}", jobStatus.JobId, jobStatus.State);

        int result;
        bool hasFinished = false;
        
        var job = await _context.Jobs.FirstOrDefaultAsync(
            j => j.State == Job.JobState.Executing && j.Id == jobStatus.JobId, cancellationToken);
        if (job != null)
        {
            switch (jobStatus.State)
            {
                case Job.JobState.Executing:
                    job.LastReported = DateTime.UtcNow;
                    break;
                
                case Job.JobState.DoneFailure:
                    _logger.LogInformation("job {jobId} is not done", jobStatus.JobId);
                    /*
                     * Job failed. Can be executed once again. We do not remember the
                     * previous failure of the job.
                     */
                    // TXWTODO: Include something like number of retries? To not jam the pipeline with an erranous job?
                    job.State = Job.JobState.Ready;
                    job.Owner = "";
                    hasFinished = true;
                    break;
                
                case Job.JobState.DoneSuccess:
                    _logger.LogInformation("job {jobId} is done", jobStatus.JobId);
                    job.State = Job.JobState.DoneSuccess;
                    job.Owner = "";
                    hasFinished = true;
                    break;
            }
            
            await _context.SaveChangesAsync(cancellationToken);

            result = 0;
        }
        else
        {
            _logger.LogInformation("job {jobId} not found", jobStatus.JobId);
            
            /*
             * We consider it to be non-fatal to receive status reports for non-existing jobs.
             * This may happen due to restarts.
             * However, this is an error we reflect.
             */
            result = -1;
        }

        if (hasFinished)
        {
            /*
             * Inform all workers there might be a new job available right now.
             */
            await _hannibalHub.Clients.All.SendAsync("NewJobAvailable");
        }

        return new Result
        {
            Status = result
        };

    }
}