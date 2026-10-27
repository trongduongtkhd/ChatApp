using ChatApp.ChatService.Dtos;

namespace ChatApp.ChatService.Hubs;

// Các method SERVER gọi xuống CLIENT (DESIGN mục 5, bảng hub /hubs/chat, hướng Server → Client).
// Hub<IChatClient> → gọi Clients.Group(...).ReceiveMessage(dto) thay vì SendAsync("ReceiveMessage", dto):
// gõ sai tên method là lỗi biên dịch ngay, không phải đến lúc chạy client mới thấy im lặng.
public interface IChatClient
{
    Task ReceiveMessage(MessageDto message);

    // Gửi tới các phòng mà user đã JoinGroup: online khi vào phòng, offline khi kết nối cuối cùng đóng.
    Task PresenceSnapshot(ChatApp.ChatService.Dtos.PresenceSnapshotDto snapshot);
    Task TypingChanged(Guid groupId, Guid userId, string displayName, bool isTyping);
    Task GroupAccessRevoked(Guid groupId);
}
