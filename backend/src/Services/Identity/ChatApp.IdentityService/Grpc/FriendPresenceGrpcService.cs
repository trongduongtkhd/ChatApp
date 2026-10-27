using ChatApp.Contracts.Grpc;
using ChatApp.IdentityService.Data;
using ChatApp.IdentityService.Entities;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.IdentityService.Grpc;

// Cổng nội bộ giống Group gRPC; không có route Gateway. UserId do Chat lấy từ JWT.
public sealed class FriendPresenceGrpcService(IdentityDbContext db) : FriendPresence.FriendPresenceBase
{
    public override async Task<PresenceUsers> GetFriends(FriendPresenceRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id không hợp lệ"));
        var friends = await db.Friendships.AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.UserLowId == userId || f.UserHighId == userId))
            .Select(f => f.UserLowId == userId ? f.UserHighId : f.UserLowId).ToListAsync(context.CancellationToken);
        var reply = new PresenceUsers();
        reply.UserIds.AddRange(friends.Select(u => u.ToString()));
        return reply;
    }
}
