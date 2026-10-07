namespace ChatApp.NotificationService.Entities;

// Số tin chưa đọc của MỘT user trong MỘT nhóm. PK kép (UserId, GroupId).
// UserId, GroupId là Id bên identity/group-service → KHÔNG có foreign key (khác database).
public class UnreadCounter
{
    public Guid UserId { get; set; }

    public Guid GroupId { get; set; }

    // Tăng 1 mỗi khi có chat.message-sent của nhóm (trừ người gửi), về 0 khi user đánh dấu đã đọc.
    public int UnreadCount { get; set; }

    // Mốc "đã đọc tới tin có SequenceNumber nào" (seq do chat-service cấp bằng Redis INCR).
    // Sự kiện có seq <= mốc này đến muộn thì KHÔNG đếm (user đã đọc rồi).
    public long LastReadSequence { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
