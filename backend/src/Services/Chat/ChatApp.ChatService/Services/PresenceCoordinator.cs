using System.Collections.Concurrent;
using ChatApp.ChatService.Dtos;
using ChatApp.ChatService.Hubs;
using ChatApp.Contracts.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;

namespace ChatApp.ChatService.Services;

// Chỉ giữ các connection CỤC BỘ. Mỗi replica gửi snapshot đến connection của chính nó,
// nên không nhân đôi sự kiện khi cùng nghe Redis Pub/Sub và dùng SignalR Backplane.
public sealed class PresenceCoordinator(
    Presence.PresenceClient presence, FriendPresence.FriendPresenceClient identity,
    GroupMembership.GroupMembershipClient groups, IHubContext<ChatHub, IChatClient> hub,
    ILogger<PresenceCoordinator> logger)
{
    public sealed class Session(Guid userId)
    {
        public Guid UserId { get; } = userId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ConcurrentDictionary<Guid, byte> Rooms { get; } = new();
        public HashSet<Guid> Watched { get; set; } = [];
        public long Version;
        public bool Ended;
    }
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private PresenceSettings currentSettings = new() { HeartbeatSeconds = 10, LeaseSeconds = 60 };
    public void Connect(string connectionId, Guid userId) => sessions.TryAdd(connectionId, new Session(userId));
    // Ngắt transport: giữ lease để reconnect không nhấp nháy offline. Chỉ EndSession xóa lease ngay.
    public void Disconnect(string connectionId)
    {
        if (sessions.TryRemove(connectionId, out var session)) session.Ended = true;
    }
    public void TrackRoom(string connectionId, Guid groupId) { if (sessions.TryGetValue(connectionId, out var s)) s.Rooms[groupId] = 0; }
    public void LeaveRoom(string connectionId, Guid groupId) { if (sessions.TryGetValue(connectionId, out var s)) s.Rooms.TryRemove(groupId, out _); }
    public bool InRoom(string connectionId, Guid groupId) => sessions.TryGetValue(connectionId, out var s) && s.Rooms.ContainsKey(groupId);

    public async Task EndAsync(string connectionId)
    {
        if (!sessions.TryGetValue(connectionId, out var session)) return;
        await session.Gate.WaitAsync();
        try
        {
            session.Ended = true;
            await presence.EndSessionAsync(new PresenceSession { UserId = session.UserId.ToString(), ConnectionId = connectionId },
                deadline: DateTime.UtcNow.AddSeconds(2));
        }
        catch (Exception ex) { logger.LogWarning(ex, "Không thu hồi được presence; lease sẽ tự hết hạn"); }
        finally { session.Gate.Release(); }
    }

    public Task HeartbeatAsync(string connectionId) => RefreshAsync(connectionId, true);

    public async Task RefreshInterestedAsync(Guid? userId = null)
    {
        // Chạy có giới hạn, tránh một chuyển trạng thái tạo hàng nghìn RPC cùng lúc.
        await Parallel.ForEachAsync(sessions.ToArray(), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (pair, ct) =>
        {
            if (userId is null || pair.Value.Watched.Contains(userId.Value))
                await RefreshAsync(pair.Key, false);
        });
    }

    private async Task RefreshAsync(string connectionId, bool heartbeat)
    {
        if (!sessions.TryGetValue(connectionId, out var session)) return;
        // Snapshot theo thứ tự cho từng connection; bỏ tín hiệu Pub/Sub lặp nếu đang refresh.
        if (!await session.Gate.WaitAsync(heartbeat ? 2000 : 0)) return;
        try
        {
            if (session.Ended) return;
            var settings = currentSettings;
            if (heartbeat)
            {
                settings = await presence.HeartbeatAsync(new PresenceSession { UserId = session.UserId.ToString(), ConnectionId = connectionId },
                    deadline: DateTime.UtcNow.AddSeconds(2));
                currentSettings = settings;
            }

            // Luôn đọc quyền từ nguồn, không tin danh sách userId do frontend đưa lên.
            var groupTask = groups.GetPresenceAudienceAsync(new PresenceAudienceRequest { UserId = session.UserId.ToString() },
                deadline: DateTime.UtcNow.AddSeconds(2)).ResponseAsync;
            var friendTask = identity.GetFriendsAsync(new FriendPresenceRequest { UserId = session.UserId.ToString() },
                deadline: DateTime.UtcNow.AddSeconds(2)).ResponseAsync;
            await Task.WhenAll(groupTask, friendTask);
            var audience = (await groupTask).Groups.ToDictionary(g => Guid.Parse(g.GroupId), g => g.UserIds.Select(Guid.Parse).ToArray());
            var friends = (await friendTask).UserIds.Select(Guid.Parse).ToArray();
            // Thu hồi phòng từ trạng thái hiện tại, không áp mù một Kafka event cũ.
            foreach (var room in session.Rooms.Keys.Where(g => !audience.ContainsKey(g)))
            {
                await hub.Groups.RemoveFromGroupAsync(connectionId, ChatHub.RoomName(room));
                session.Rooms.TryRemove(room, out _);
                await hub.Clients.Client(connectionId).GroupAccessRevoked(room);
            }
            var allowed = audience.Values.SelectMany(ids => ids).Concat(friends).Append(session.UserId).Distinct().ToArray();
            session.Watched = allowed.ToHashSet(); // thay reference, không sửa HashSet đang được luồng khác đọc
            var query = new PresenceUsers();
            query.UserIds.AddRange(allowed.Select(id => id.ToString()));
            var result = await presence.GetOnlineAsync(query, deadline: DateTime.UtcNow.AddSeconds(2));
            var online = result.UserIds.Select(Guid.Parse).ToHashSet();
            var counts = audience.ToDictionary(g => g.Key, g => g.Value.Count(id => id != session.UserId && online.Contains(id)));
            if (!session.Ended)
                await hub.Clients.Client(connectionId).PresenceSnapshot(new(true, ++session.Version,
                    settings.HeartbeatSeconds, settings.LeaseSeconds, online.ToArray(), friends, counts));
        }
        catch (Exception ex)
        {
            logger.LogWarning("Presence chưa xác định cho {ConnectionId}: {Error}", connectionId, ex.Message);
            session.Watched = [];
            if (!session.Ended)
            {
                try { await hub.Clients.Client(connectionId).PresenceSnapshot(new(false, ++session.Version, currentSettings.HeartbeatSeconds, currentSettings.LeaseSeconds, [], [], new Dictionary<Guid, int>())); }
                catch (Exception sendEx) { logger.LogDebug(sendEx, "Không gửi được snapshot unknown"); }
            }
        }
        finally { session.Gate.Release(); }
    }
}
