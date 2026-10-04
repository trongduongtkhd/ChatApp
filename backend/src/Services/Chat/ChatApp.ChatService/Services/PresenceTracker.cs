using StackExchange.Redis;

namespace ChatApp.ChatService.Services;

// Theo dõi ai đang online (DESIGN mục 3: set presence:{userId} chứa các ConnectionId SignalR đang mở).
//
// Vì sao lưu TẬP ConnectionId mà không lưu cờ true/false?
// Một user có thể mở nhiều kết nối (2 tab, điện thoại + máy tính). Đóng 1 tab KHÔNG có nghĩa là offline.
// User online ⇔ set còn ít nhất 1 phần tử. Set rỗng → Redis tự xóa key → offline.
//
// Vì sao lưu ở Redis mà không lưu trong RAM của chat-service?
// Phần 8 chạy 2 bản: tab 1 nối vào bản A, tab 2 nối vào bản B. Chỉ có nơi lưu chung mới biết user còn kết nối nào không.
public class PresenceTracker(IConnectionMultiplexer redis)
{
    // SREM rồi SCARD trong MỘT bước nguyên tử. Nếu làm 2 lệnh riêng từ C#, 2 tab đóng cùng lúc có thể:
    //   tab1 SREM, tab2 SREM, tab1 SCARD = 0, tab2 SCARD = 0 → báo "offline" 2 lần;
    // hoặc ngược lại tab1 SCARD đọc trước khi tab2 SREM → không ai thấy 0 → không bao giờ báo offline.
    private const string RemoveAndCountScript = """
        redis.call('SREM', KEYS[1], ARGV[1])
        return redis.call('SCARD', KEYS[1])
        """;

    public static string Key(Guid userId) => $"presence:{userId}";

    // Trả về số kết nối của user sau khi thêm (1 = vừa chuyển từ offline sang online).
    public async Task<long> ConnectedAsync(Guid userId, string connectionId)
    {
        var db = redis.GetDatabase();
        await db.SetAddAsync(Key(userId), connectionId);
        return await db.SetLengthAsync(Key(userId));
    }

    // Trả về true nếu đây là kết nối CUỐI CÙNG của user (user vừa chuyển sang offline).
    public async Task<bool> DisconnectedAsync(Guid userId, string connectionId)
    {
        var remaining = (long)await redis.GetDatabase().ScriptEvaluateAsync(
            RemoveAndCountScript, [Key(userId)], [connectionId]);
        return remaining == 0;
    }

    // Lọc ra những user đang online trong danh sách (vd thành viên một nhóm).
    // Gửi tất cả lệnh EXISTS cùng lúc rồi mới chờ (pipelining): N user ≈ thời gian 1 lần đi về Redis, không phải N lần.
    public async Task<IReadOnlyList<Guid>> GetOnlineAsync(IEnumerable<Guid> userIds)
    {
        var db = redis.GetDatabase();
        var checks = userIds.Select(id => (id, exists: db.KeyExistsAsync(Key(id)))).ToList();
        await Task.WhenAll(checks.Select(c => c.exists));
        return checks.Where(c => c.exists.Result).Select(c => c.id).ToList();
    }
}
