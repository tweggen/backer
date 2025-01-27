using Higgins.Configuration;
using Higgins.Data;
using Higgins.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Higgins.Services;

public class HigginsService : IHigginsService
{
    private readonly HigginsContext _context;
    private readonly ILogger<HigginsService> _logger;
    private readonly HigginsServiceOptions _options;

    
    public HigginsService(
        HigginsContext context,
        ILogger<HigginsService> logger,
        IOptions<HigginsServiceOptions> options)
    {
        _context = context;
        _logger = logger;
        _options = options.Value;
    }


    public async Task<CreateEndpointResult> CreateEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken)
    {
        await _context.Endpoints.AddAsync(endpoint);
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateEndpointResult() { Id = endpoint.Id };
    }


    public async Task<Endpoint> GetEndpointAsync(
        string name,
        CancellationToken cancellationToken)
    {
        Endpoint? endpoint = await _context.Endpoints.FirstOrDefaultAsync(e => e.Name == name);
        if (null == endpoint)
        {
            throw new KeyNotFoundException($"No endpoint found for name {name}");
        }

        return endpoint;
    }
}