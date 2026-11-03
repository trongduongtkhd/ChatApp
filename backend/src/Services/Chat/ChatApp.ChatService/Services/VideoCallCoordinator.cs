using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatApp.ChatService.Dtos;
using ChatApp.ChatService.Hubs;
using ChatApp.Common.Auth;
using ChatApp.Contracts;
using ChatApp.Contracts.Grpc;
using Microsoft.AspNetCore.SignalR;

namespace ChatApp.ChatService.Services;

public sealed class VideoCallCoordinator(VideoCallStore store, GroupMemberCache members,
    FriendPresence.FriendPresenceClient friends, IAccountAccessReader accounts,
    IHubContext<ChatHub, IChatClient> hub, IConfiguration config)
{
    public async Task<VideoCallConfig> ConfigAsync(string user, string conn)
    {
        await store.RegisterAsync(user, conn);
        var servers = new List<VideoIceServer>();
        var stun = config.GetSection("VideoCalls:StunUrls").Get<string[]>()?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? [];
        if (stun.Length > 0) servers.Add(new(stun));
        var turn = config.GetSection("VideoCalls:TurnUrls").Get<string[]>()?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? [];
        if (turn.Length > 0)
        {
            var secret = config["VideoCalls:TurnSharedSecret"];
            if (string.IsNullOrWhiteSpace(secret)) throw new HubException("TURN chưa có khóa cấu hình trên backend.");
            var username = $"{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}:{user}";
            var credential = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
            servers.Add(new(turn, username, credential));
        }
        var policy = config["VideoCalls:IceTransportPolicy"] == "relay" ? "relay" : "all";
        if (policy == "relay" && turn.Length == 0) throw new HubException("Chế độ relay cần máy chủ TURN đã cấu hình.");
        return new(conn, servers.ToArray(), policy);
    }

    private async Task<IReadOnlyDictionary<Guid, AccountAccessState>> CheckAsync(Guid caller, Guid callee, Guid group, CancellationToken ct)
    {
        var states = await accounts.ReadAsync([caller, callee], ct);
        if (states.GetValueOrDefault(caller) is not { IsLocked: false } || states.GetValueOrDefault(callee) is not { IsLocked: false })
            throw new HubException("Tài khoản không thể tham gia cuộc gọi.");
        var accepted = await friends.GetFriendsAsync(new FriendPresenceRequest { UserId = caller.ToString() }, deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: ct);
        if (!accepted.UserIds.Contains(callee.ToString()) || group != DirectChat.GroupIdFor(caller, callee)
            || !await members.IsMemberAsync(group, caller, ct) || !await members.IsMemberAsync(group, callee, ct))
            throw new HubException("Chỉ gọi video trong cuộc trò chuyện riêng của hai người đang là bạn bè.");
        return states;
    }
    public async Task ValidateAsync(VideoCallState call, CancellationToken ct)
    {
        var caller = Guid.Parse(call.CallerId); var callee = Guid.Parse(call.CalleeId);
        var states = await CheckAsync(caller, callee, Guid.Parse(call.GroupId), ct);
        if (states[caller].AuthVersion != call.CallerVersion || states[callee].AuthVersion != call.CalleeVersion)
            throw new HubException("Phiên đăng nhập của cuộc gọi đã thay đổi.");
    }
    public async Task<VideoCallDto> StartAsync(Guid id, Guid peer, ClaimsPrincipal user, string conn, CancellationToken ct)
    {
        var caller = user.GetUserId();
        if (id == Guid.Empty || peer == Guid.Empty || peer == caller) throw new HubException("Người nhận hoặc mã cuộc gọi không hợp lệ.");
        var group = DirectChat.GroupIdFor(caller, peer);
        var states = await CheckAsync(caller, peer, group, ct);
        if (!AccountAccessRules.Allows(user, states[caller])) throw new HubException("Phiên đăng nhập đã thay đổi.");
        var call = await store.ChangeAsync("start", id.ToString(), caller.ToString(), conn,
            new { calleeId = peer.ToString(), groupId = group.ToString(), callerName = user.GetDisplayName(), callerVersion = states[caller].AuthVersion, calleeVersion = states[peer].AuthVersion });
        await NotifyAsync(call); return call.ToDto();
    }
    public async Task<VideoCallDto?> CurrentAsync(string user) => (await store.CurrentAsync(user))?.ToDto();
    public async Task<VideoCallDto> ChangeAsync(string op, Guid id, ClaimsPrincipal user, string conn, bool connected, string reason, CancellationToken ct)
    {
        var call = await store.ReadAsync(id.ToString()) ?? throw new HubException("Cuộc gọi không còn hiệu lực.");
        var uid = user.GetUserId().ToString();
        if (uid != call.CallerId && uid != call.CalleeId) throw new HubException("Bạn không thuộc cuộc gọi này.");
        if (op != "end" && call.State != "Ended") await ValidateAsync(call, ct);
        var changed = await store.ChangeAsync(op, id.ToString(), uid, conn, new { connected, reason, name = user.GetDisplayName() });
        // Pulse trả snapshot cho đúng connection; chỉ broadcast khi state/version đổi.
        if (changed.Version != call.Version || op != "pulse") await NotifyAsync(changed);
        return changed.ToDto();
    }
    public async Task SignalAsync(Guid id, string kind, string data, ClaimsPrincipal user, string conn, CancellationToken ct)
    {
        if (kind is not ("offer" or "answer" or "ice") || string.IsNullOrEmpty(data) || data.Length > (kind == "ice" ? 4096 : 24000))
            throw new HubException("Tín hiệu WebRTC không hợp lệ hoặc quá dài.");
        if (kind == "ice")
        {
            try { using var json = JsonDocument.Parse(data); if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("candidate", out _)) throw new JsonException(); }
            catch (JsonException) { throw new HubException("ICE candidate không hợp lệ."); }
        }
        else if (!data.StartsWith("v=0", StringComparison.Ordinal)) throw new HubException("SDP không hợp lệ.");
        var call = await store.ChangeAsync("signal", id.ToString(), user.GetUserId().ToString(), conn, new { kind });
        if (call.State == "Ended") throw new HubException("Cuộc gọi đã kết thúc.");
        await ValidateAsync(call, ct);
        var target = conn == call.CallerConnectionId ? call.CalleeConnectionId : call.CallerConnectionId;
        await hub.Clients.Client(target).VideoCallSignal(new(id.ToString(), kind, data));
    }
    public async Task NotifyAsync(VideoCallState call)
    {
        var targets = (await store.LiveAsync(call.CalleeId)).Append(call.CallerConnectionId)
            .Append(call.CalleeConnectionId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToArray();
        await hub.Clients.Clients(targets).VideoCallChanged(call.ToDto());
    }
    public async Task DisconnectAsync(string user, string conn)
    {
        var call = await store.DisconnectAsync(user, conn); if (call is not null) await NotifyAsync(call);
    }
    public async Task ForceEndAsync(VideoCallState call, string reason)
    { var ended = await store.ChangeAsync("force-end", call.Id, "", "", new { reason }); await NotifyAsync(ended); }
}
