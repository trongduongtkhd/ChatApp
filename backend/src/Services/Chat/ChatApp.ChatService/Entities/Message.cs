namespace ChatApp.ChatService.Entities;

public class Message
{
    // DO CLIENT SINH (Angular tạo GUID trước khi gửi), KHÔNG để server tự sinh.
    // Client gửi lại cùng một tin (mạng chập chờn, tự reconnect) vẫn mang cùng Id
    // → server nhận ra tin trùng và bỏ qua (idempotency, Bước 3).
    public Guid Id { get; set; }

    public Guid GroupId { get; set; }

    // UserId bên identity-service, lấy từ claim "sub" của JWT. KHÔNG có foreign key (khác database).
    public Guid SenderId { get; set; }

    // Tên hiển thị lúc gửi (claim "display_name"), lưu kèm để hiển thị lịch sử
    // mà không phải hỏi identity-service. Người gửi đổi tên sau này thì tin cũ vẫn giữ tên cũ.
    public string SenderName { get; set; } = null!;

    public string Content { get; set; } = null!;

    // Số thứ tự trong nhóm, cấp bởi Redis INCR chat:seq:{groupId} (Bước 2).
    // Dùng để SẮP XẾP tin nhắn và phân trang lịch sử.
    public long SequenceNumber { get; set; }

    // Chỉ để HIỂN THỊ giờ gửi. Không dùng để sắp xếp: khi chạy nhiều bản chat-service,
    // đồng hồ mỗi máy có thể lệch nhau vài trăm ms → sắp theo giờ có thể sai thứ tự.
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
