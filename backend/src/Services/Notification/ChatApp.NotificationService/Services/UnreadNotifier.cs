using ChatApp.NotificationService.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChatApp.NotificationService.Services;

// Đẩy UnreadCountChanged xuống trình duyệt. Dùng IHubContext: gửi qua hub từ NGOÀI hub
// (từ consumer Kafka chạy nền, từ controller REST).
//
// Luôn gọi SAU khi transaction đã commit: client nhận số mới thì số đó chắc chắn đã nằm trong DB
// (GET /unread lúc đó trả cùng số). Gửi lỗi (user offline, mạng chập chờn) thì CHỈ ghi log:
// - không ném lỗi ra consumer → không làm Kafka thử lại một sự kiện đã xử lý xong;
// - SignalR không gửi bù → client lấy lại số đúng bằng GET /unread khi kết nối lại.
public sealed class UnreadNotifier(
    IHubContext<NotificationHub, INotificationClient> hub,
    ILogger<UnreadNotifier> logger)
{
    public async Task NotifyAsync(Guid groupId, IEnumerable<UnreadChange> changes)
    {
        foreach (var change in changes)
        {
            try
            {
                // User không có kết nối nào → SignalR bỏ qua, không lỗi.
                await hub.Clients.User(change.UserId.ToString()).UnreadCountChanged(groupId, change.UnreadCount);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Không đẩy được UnreadCountChanged tới user {UserId} (nhóm {GroupId})",
                    change.UserId, groupId);
            }
        }
    }
}
