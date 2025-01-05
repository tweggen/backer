using Higgins.Configuration;
using Higgins.Data;
using Higgins.Models;
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
        _context.Endpoints.Add(endpoint);
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateEndpointResult() { Id = endpoint.Id };
    }


    public async Task<CreateRouteResult> CreateRouteAsync(
        Route route,
        CancellationToken cancellationToken)
    {
        _context.Routes.Add(route);
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateRouteResult() { Id = route.Id };
    }
}