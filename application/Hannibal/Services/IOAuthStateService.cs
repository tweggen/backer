using Hannibal.Models;


namespace Hannibal.Services;

public interface IOAuthStateService
{
    Task<Guid> CreateAsync(string userId, string provider, string returnUrl, CancellationToken ct);
    Task<OAuthState?> ValidateAsync(Guid stateId, string provider, CancellationToken ct);
    Task MarkUsedAsync(Guid stateId, CancellationToken ct);
}