using Hannibal.Configuration;
using Hannibal.Models;
using Microsoft.Extensions.Options;

namespace Hannibal.Services;


public class HannibalService : IHannibalService
{
    private readonly ILogger<HannibalService> _logger;
    private readonly HannibalServiceOptions _options;

    
    public HannibalService(
        ILogger<HannibalService> logger,
        IOptions<HannibalServiceOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    
    public async Task<Job> AcquireNextJobAsync(string capabilities, CancellationToken cancellationToken)
    {
        _logger.LogInformation("new job requested by for client with capas {capabilities}", capabilities);
        // Implement backup logic
        return new Job 
        { 
            Id = 1, FromUri = "file:///tmp/a", ToUri = "file:///tmp/b", State = Job.JobState.Ready 
        };
    }


    public async Task<ShutdownResult> ShutdownAsync()
    {
        return new ShutdownResult() { ErrorCode = 0 };
    }
}