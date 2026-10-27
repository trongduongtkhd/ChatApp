using ChatApp.ChatService.Grpc;
using StackExchange.Redis;

namespace ChatApp.ChatService.Services;

// Tên lớp giữ từ Phần 7. Công việc 3: IsMember luôn hỏi nguồn Group gRPC hiện tại.
// Cache-aside bên dưới chỉ là đường đọc danh sách legacy, không được dùng quyết định quyền.
// Invalidate xóa key legacy và đánh thức đồng bộ phòng trên mọi replica qua Pub/Sub.
public class GroupMemberCache(
    IConnectionMultiplexer redis,
    GroupMembershipClient groups,
    ILogger<GroupMemberCache> logger)
{
    // Lưới an toàn: lỡ mất sự kiện Kafka (hoặc ghi cache cũ đè lên, xem FillAsync) thì cache cũng chỉ sai tối đa 10 phút.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static string Key(Guid groupId) => $"group:members:{groupId}";

    public async Task<bool> IsMemberAsync(Guid groupId, Guid userId, CancellationToken ct)
    {
        // Công việc 3: quyền truy cập phải theo nguồn hiện tại, cả khi Kafka đang ngừng.
        return await groups.IsMemberAsync(groupId, userId, ct);
    }

    // Danh sách thành viên (Bước 6 – presence dùng).
    public async Task<IReadOnlyList<Guid>> GetMemberIdsAsync(Guid groupId, CancellationToken ct)
    {
        var cached = await redis.GetDatabase().SetMembersAsync(Key(groupId));
        if (cached.Length > 0)
            return cached.Select(v => Guid.Parse(v.ToString())).ToList();
        return await FillAsync(groupId, ct);
    }

    // Gọi khi có member-added / member-removed: xóa thay vì sửa tại chỗ (đơn giản, không lo sai thứ tự sự kiện
    // giữa 2 topic added/removed: lần đọc sau luôn lấy danh sách MỚI NHẤT từ group-service).
    public async Task InvalidateAsync(Guid groupId)
    {
        var deleted = await redis.GetDatabase().KeyDeleteAsync(Key(groupId));
        // Mọi replica phải kiểm tra lại phòng + audience; consumer group Kafka chỉ giao cho một replica.
        await redis.GetSubscriber().PublishAsync(RedisChannel.Literal(PresenceRelay.MembershipChannel), groupId.ToString());
        logger.LogInformation("Xóa cache thành viên {Key}: {Result}", Key(groupId), deleted ? "đã xóa" : "không có sẵn");
    }

    private async Task<IReadOnlyList<Guid>> FillAsync(Guid groupId, CancellationToken ct)
    {
        // Cache miss → hỏi group-service (nguồn gốc). Lỗi gRPC (group-service chết) ném ra cho hub xử lý.
        var memberIds = await groups.GetMemberIdsAsync(groupId, ct);

        // Nhóm rỗng (không tồn tại/đã xóa): Redis không lưu được set rỗng → không cache, lần sau lại hỏi gRPC.
        if (memberIds.Count == 0)
            return memberIds;

        // MULTI/EXEC: SADD và EXPIRE chạy liền một khối → không bao giờ có key nằm trong Redis mà thiếu TTL
        // (vd service sập đúng giữa 2 lệnh → key sống mãi).
        // Khe hở đã biết: đọc gRPC xong, CHƯA kịp ghi thì có sự kiện xóa cache đi qua → ta ghi đè danh sách cũ.
        // Cache sai tới sự kiện tiếp theo hoặc hết TTL. Chấp nhận vì hiếm và có TTL chặn trên.
        var tx = redis.GetDatabase().CreateTransaction();
        var key = Key(groupId);
        _ = tx.SetAddAsync(key, memberIds.Select(id => (RedisValue)id.ToString()).ToArray());
        _ = tx.KeyExpireAsync(key, Ttl);
        await tx.ExecuteAsync();

        logger.LogInformation("Nạp cache {Key} từ gRPC: {Count} thành viên", key, memberIds.Count);
        return memberIds;
    }
}
