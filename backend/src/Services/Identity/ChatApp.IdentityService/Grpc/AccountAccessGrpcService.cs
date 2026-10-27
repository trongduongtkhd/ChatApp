using ChatApp.Common.Auth;
using ChatApp.Contracts.Grpc;
using Grpc.Core;

namespace ChatApp.IdentityService.Grpc;

public sealed class AccountAccessGrpcService(IAccountAccessReader reader) : AccountAccess.AccountAccessBase
{
    public override async Task<AccountAccessReply> GetAccounts(AccountAccessRequest request, ServerCallContext context)
    {
        if (request.UserIds.Count > 500 || request.UserIds.Any(id => !Guid.TryParse(id, out _)))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Tối đa 500 userId hợp lệ."));
        var rows = await reader.ReadAsync(request.UserIds.Select(Guid.Parse).Distinct().ToArray(), context.CancellationToken);
        var reply = new AccountAccessReply();
        reply.Accounts.AddRange(rows.Values.Select(a => new AccountState
        {
            UserId = a.UserId.ToString(), IsLocked = a.IsLocked, AuthVersion = a.AuthVersion, SystemRole = a.SystemRole
        }));
        return reply;
    }
}
