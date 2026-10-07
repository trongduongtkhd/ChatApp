namespace ChatApp.NotificationService.Hubs;

// Các method SERVER gọi xuống CLIENT (DESIGN mục 5). Hub có kiểu (Hub<T>) → gõ sai tên method là lỗi biên dịch,
// không phải lỗi lặng lẽ lúc chạy (client không nhận được gì mà không biết vì sao).
public interface INotificationClient
{
    Task UnreadCountChanged(Guid groupId, int unreadCount);
}
