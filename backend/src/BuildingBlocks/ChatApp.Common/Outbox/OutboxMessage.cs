namespace ChatApp.Common.Outbox;

// Một dòng bảng outbox_messages = một sự kiện CHỜ gửi lên Kafka.
// Được ghi cùng transaction với dữ liệu nghiệp vụ (vd INSERT users + INSERT outbox_messages)
// → hoặc cả hai cùng có, hoặc cả hai cùng không. Không bao giờ có user mà mất sự kiện.
public class OutboxMessage
{
    // = EventId của sự kiện. Gửi lại bao nhiêu lần, bên nghe vẫn thấy cùng một EventId.
    public Guid Id { get; set; }

    public string Topic { get; set; } = "";

    // Kafka key (userId, groupId...) → quyết định partition.
    public string Key { get; set; } = "";

    public string EventType { get; set; } = "";

    // Nội dung JSON gửi nguyên văn lên Kafka (cột jsonb, xem/truy vấn được bằng SQL).
    public string Payload { get; set; } = "";

    public DateTimeOffset OccurredAt { get; set; }

    // null = chưa gửi. Có giá trị = Kafka đã xác nhận ghi.
    public DateTimeOffset? ProcessedAt { get; set; }

    // Số lần đã thử gửi (kể cả lần thành công).
    public int Attempts { get; set; }

    // Lỗi của lần gửi thất bại gần nhất (vd "Local: Message timed out" khi Kafka chết).
    public string? LastError { get; set; }
}
