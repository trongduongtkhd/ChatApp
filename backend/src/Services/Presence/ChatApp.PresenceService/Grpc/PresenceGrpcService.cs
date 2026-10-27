using ChatApp.Contracts.Grpc;
using ChatApp.PresenceService.Services;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace ChatApp.PresenceService.Grpc;

public sealed class PresenceGrpcService(PresenceStore store, IOptions<PresenceOptions> options)
    : Presence.PresenceBase
{
    public override async Task<PresenceSettings> Heartbeat(PresenceSession request, ServerCallContext context)
    {
        ValidateSession(request);
        await store.HeartbeatAsync(Guid.Parse(request.UserId), request.ConnectionId);
        return new PresenceSettings { HeartbeatSeconds = options.Value.HeartbeatSeconds, LeaseSeconds = options.Value.LeaseSeconds };
    }

    public override async Task<PresenceEmpty> EndSession(PresenceSession request, ServerCallContext context)
    {
        ValidateSession(request);
        await store.EndSessionAsync(Guid.Parse(request.UserId), request.ConnectionId);
        return new PresenceEmpty();
    }

    public override async Task<PresenceUsers> GetOnline(PresenceUsers request, ServerCallContext context)
    {
        if (request.UserIds.Count > 20000 || request.UserIds.Any(u => !Guid.TryParse(u, out _)))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Danh sách user không hợp lệ"));
        var reply = new PresenceUsers();
        reply.UserIds.AddRange(await store.GetOnlineAsync(request.UserIds));
        return reply;
    }

    private static void ValidateSession(PresenceSession request)
    {
        if (!Guid.TryParse(request.UserId, out _) || string.IsNullOrWhiteSpace(request.ConnectionId) || request.ConnectionId.Length > 200)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Phiên không hợp lệ"));
    }
}
