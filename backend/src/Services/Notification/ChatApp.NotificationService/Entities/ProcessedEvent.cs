namespace ChatApp.NotificationService.Entities;

// Sổ ghi "sự kiện này đã xử lý rồi" cho Idempotent Consumer (Bước 3).
// Kafka giao at-least-once → cùng một sự kiện (cùng EventId) có thể đến 2 lần.
// Ghi EventId vào đây CÙNG transaction với UnreadCount + 1: lần thứ 2 gặp PK trùng → bỏ qua.
public class ProcessedEvent
{
    // = IntegrationEvent.EventId do bên phát sinh (GUID v7).
    public Guid EventId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}
