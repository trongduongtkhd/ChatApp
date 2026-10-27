using ChatApp.Common.Auth;
using ChatApp.IdentityService.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.IdentityService.Services;

public sealed class LocalAccountAccessReader(IdentityDbContext db) : IAccountAccessReader
{
    public async Task<IReadOnlyDictionary<Guid, AccountAccessState>> ReadAsync(IReadOnlyCollection<Guid> users, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => users.Contains(u.Id))
            .Select(u => new AccountAccessState(u.Id, u.IsLocked, u.AuthVersion, u.SystemRole))
            .ToDictionaryAsync(u => u.UserId, ct);
}
