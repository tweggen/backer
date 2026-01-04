using Hannibal.Data;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;


namespace Hannibal.Services;

public class OAuthStateService : IOAuthStateService
{
    private readonly HannibalContext _db;

    public OAuthStateService(HannibalContext db)
    {
        _db = db;
    }

    public async Task<Guid> CreateAsync(string userId, string provider, string returnUrl, CancellationToken ct)
    {
        var state = new OAuthState
        {
            UserId = userId,
            Provider = provider,
            ReturnUrl = returnUrl
        };

        _db.OAuthStates.Add(state);
        await _db.SaveChangesAsync(ct);

        return state.Id;
    }

    public async Task<OAuthState?> ValidateAsync(Guid stateId, string provider, CancellationToken ct)
    {
        var state = await _db.OAuthStates
            .FirstOrDefaultAsync(x => x.Id == stateId, ct);

        if (state == null)
            return null;

        if (state.Provider != provider)
            return null;

        if (state.Used)
            return null;

        // Optional: expire after 10 minutes
        if (DateTime.UtcNow - state.CreatedAt > TimeSpan.FromMinutes(10))
            return null;

        return state;
    }

    public async Task MarkUsedAsync(Guid stateId, CancellationToken ct)
    {
        var state = await _db.OAuthStates.FindAsync(new object[] { stateId }, ct);
        if (state == null)
            return;

        state.Used = true;
        await _db.SaveChangesAsync(ct);
    }
}