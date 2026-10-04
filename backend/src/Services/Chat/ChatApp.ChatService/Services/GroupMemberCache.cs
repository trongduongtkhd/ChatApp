using ChatApp.ChatService.Grpc;
using StackExchange.Redis;

namespace ChatApp.ChatService.Services;

// Cache danh sách thành viên nhóm trong Redis (DESIGN mục 3: set group:members:{groupId}), mẫu CACHE-ASIDE:
//   đọc cache → có thì dùng luôn; không có (cache miss) → hỏi nguồn gốc (group-service qua gRPC) → ghi vào cache.
//
// Lợi ích:
// - Mỗi tin nhắn không phải gọi gRPC (nhanh hơn, group-service đỡ tải).
// - group-service chết thì nhóm đã có trong cache VẪN chat được (giảm phụ thuộc lúc chạy).
// Cái giá: dữ liệu có thể CŨ trong chốc lát. Thêm/xóa thành viên → group-service phát Kafka member-added/removed
// → MemberAdded/RemovedConsumer xóa key → lần sau đọc lại từ gRPC. Khoảng trễ ~1–2 giây (eventual consistency).
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
        var db = redis.GetDatabase();
        var key = Key(groupId);

        // Cache hit, là thành viên (trường hợp thường gặp nhất: 1 lệnh Redis, không gọi gRPC).
        if (await db.SetContainsAsync(key, userId.ToString()))
            return true;

        // SISMEMBER trả false cho cả 2 trường hợp "key có nhưng không chứa user" và "key không tồn tại"
        // → phải hỏi thêm EXISTS để phân biệt "chắc chắn không phải thành viên" với "chưa có cache".
        if (await db.KeyExistsAsync(key))
            return false;

        var memberIds = await FillAsync(groupId, ct);
        return memberIds.Contains(userId);
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
