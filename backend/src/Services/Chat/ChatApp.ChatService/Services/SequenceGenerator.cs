using ChatApp.ChatService.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace ChatApp.ChatService.Services;

// Cấp số thứ tự tin nhắn cho từng nhóm bằng Redis INCR (DESIGN mục 3: key chat:seq:{groupId}).
//
// Vì sao Redis mà không tự đếm trong RAM của chat-service?
// Phần 8 chạy 2 bản chat-service: mỗi bản đếm riêng thì cả hai cùng cấp số 5 → trùng.
// Redis là MỘT nơi đếm chung (bộ đếm tập trung) cho mọi bản.
//
// Vì sao INCR không bao giờ cấp trùng dù 200 request gọi cùng lúc?
// Redis xử lý lệnh trên MỘT luồng, lần lượt từng lệnh; INCR là một lệnh nguyên tử (đọc + cộng + ghi trong một bước),
// không có chuyện 2 lệnh cùng đọc giá trị 4 rồi cùng ghi 5.
//
// Nguồn sự thật là PostgreSQL (bảng messages), Redis chỉ là bộ đếm nhanh. Redis mất/cũ → tính lại từ DB.
public class SequenceGenerator(IConnectionMultiplexer redis, ChatDbContext db, ILogger<SequenceGenerator> logger)
{
    // Lua script chạy NGUYÊN TỬ trong Redis (không lệnh nào chen vào giữa GET và SET):
    // "nâng bộ đếm lên ít nhất bằng ARGV[1]", không bao giờ hạ xuống.
    // Nếu dùng GET rồi SET từ C# (2 lệnh riêng), giữa 2 lệnh bản khác có thể đã INCR → SET đè lùi số → cấp trùng.
    private const string RaiseToScript = """
        local current = tonumber(redis.call('GET', KEYS[1]) or '0')
        local floor = tonumber(ARGV[1])
        if current < floor then
            redis.call('SET', KEYS[1], floor)
            return floor
        end
        return current
        """;

    public static string Key(Guid groupId) => $"chat:seq:{groupId}";

    public async Task<long> NextAsync(Guid groupId)
    {
        var redisDb = redis.GetDatabase();
        var key = Key(groupId);

        // Key mất (Redis khởi động lại không lưu, bị xóa nhầm...) → nếu INCR luôn sẽ cấp lại 1, 2... đã dùng.
        // → Khởi tạo lại từ MAX(sequence_number) trong DB trước. Chỉ tốn 1 lệnh EXISTS mỗi tin khi key còn.
        if (!await redisDb.KeyExistsAsync(key))
            await ResyncAsync(groupId, "key không tồn tại");

        return await redisDb.StringIncrementAsync(key);
    }

    // Đưa bộ đếm lên bằng số lớn nhất đã lưu trong DB. Gọi khi:
    // - key mất (ở NextAsync), hoặc
    // - key còn nhưng giá trị CŨ hơn DB (vd Redis khôi phục từ bản sao lưu cũ) → MessageService phát hiện qua
    //   lỗi trùng unique index (group_id, sequence_number) rồi gọi hàm này và thử lại.
    public async Task ResyncAsync(Guid groupId, string reason)
    {
        var max = await db.Messages
            .Where(m => m.GroupId == groupId)
            .MaxAsync(m => (long?)m.SequenceNumber) ?? 0;

        var result = (long)await redis.GetDatabase().ScriptEvaluateAsync(
            RaiseToScript, [Key(groupId)], [max]);

        // MAX = 0: nhóm chưa có tin nào (tin đầu tiên của nhóm mới) → bình thường, không phải sự cố.
        logger.Log(max == 0 ? LogLevel.Debug : LogLevel.Warning,
            "Đồng bộ lại bộ đếm {Key} ({Reason}): MAX trong DB = {Max}, bộ đếm hiện tại = {Counter}",
            Key(groupId), reason, max, result);
    }
}
