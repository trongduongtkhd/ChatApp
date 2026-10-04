using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using ChatApp.GroupService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatApp.GroupService.Messaging;

// Nghe identity.user-registered → cập nhật bản sao user_snapshots (eventual consistency).
// Thay cho script seed tạm của Phần 4 (đọc chéo identity_db, đã xóa).
public sealed class UserRegisteredConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<UserRegisteredConsumer> logger)
    : KafkaConsumerBase<UserRegistered>(scopeFactory, options, logger)
{
    protected override string Topic => KafkaTopics.UserRegistered;

    protected override async Task HandleAsync(UserRegistered evt, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<GroupDbContext>();

        // UPSERT idempotent: nhận cùng một sự kiện 1 lần hay 10 lần (at-least-once) vẫn chỉ có MỘT dòng, cùng nội dung.
        // Một câu SQL nguyên tử → 2 bản consumer cùng chèn một user cũng không lỗi trùng khóa
        // (khác với "SELECT xem có chưa rồi mới INSERT": giữa 2 bước có thể bị chen ngang).
        // Tham số {..} được EF chuyển thành tham số SQL ($1, $2...) → không bị SQL injection.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO user_snapshots (user_id, user_name, display_name)
            VALUES ({evt.UserId}, {evt.UserName}, {evt.DisplayName})
            ON CONFLICT (user_id) DO UPDATE
            SET user_name = EXCLUDED.user_name, display_name = EXCLUDED.display_name
            """, ct);
    }
}
