using ChatApp.Common.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatApp.NotificationService.Hubs;

// Hub /hubs/notifications: kênh MỘT CHIỀU server → client. Client chỉ kết nối rồi nghe UnreadCountChanged,
// không có method nào để gọi lên (đọc/đánh dấu đã đọc đi qua REST).
// Không cần "phòng" như chat: mỗi thông báo dành cho MỘT user → gửi bằng Clients.User(userId),
// SignalR tự gửi tới MỌI kết nối của user đó (nhiều tab, nhiều máy).
[Authorize]
public class NotificationHub(ILogger<NotificationHub> logger) : Hub<INotificationClient>
{
    public override Task OnConnectedAsync()
    {
        // Context.UserIdentifier do SubUserIdProvider lấy từ claim "sub" của JWT.
        logger.LogInformation("Kết nối {ConnectionId} của user {UserName} ({UserId})",
            Context.ConnectionId, Context.User?.GetUserName(), Context.UserIdentifier);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Ngắt kết nối {ConnectionId} của user {UserId}", Context.ConnectionId, Context.UserIdentifier);
        return base.OnDisconnectedAsync(exception);
    }
}
