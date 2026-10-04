using System.Text.Json;
using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Common.Outbox;

public static class OutboxDbContextExtensions
{
    // CHỈ thêm dòng outbox vào DbContext, KHÔNG gửi Kafka, KHÔNG tự lưu.
    // Service nghiệp vụ gọi hàm này TRƯỚC SaveChangesAsync → dữ liệu + sự kiện lưu trong cùng một transaction.
    public static void AddOutboxEvent(this DbContext db, string topic, string key, IntegrationEvent evt)
    {
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = evt.EventId,
            Topic = topic,
            Key = key,
            EventType = evt.EventType,
            // evt.GetType(): chuyển theo kiểu THẬT (UserRegistered...), nếu không chỉ ra được 3 field chung của lớp gốc.
            Payload = JsonSerializer.Serialize(evt, evt.GetType(), KafkaJson.Options),
            OccurredAt = evt.OccurredAt
        });
    }
}
