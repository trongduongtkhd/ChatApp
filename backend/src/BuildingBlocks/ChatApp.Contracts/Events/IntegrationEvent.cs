namespace ChatApp.Contracts.Events;

// Lớp gốc của mọi sự kiện gửi giữa các service qua Kafka ("integration event").
// 3 field chung theo DESIGN mục 4: eventId, eventType, occurredAt.
public abstract record IntegrationEvent
{
    // Định danh DUY NHẤT của sự kiện (GUID v7 sinh phi tập trung ở bên phát).
    // Kafka giao at-least-once → bên nghe dùng EventId để nhận ra sự kiện trùng.
    // Cũng là Id của dòng outbox_messages chứa sự kiện này.
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    // Thời điểm sự kiện xảy ra ở bên phát (UTC).
    // KHÔNG dùng để sắp thứ tự sự kiện của các máy KHÁC nhau (đồng hồ các máy có thể lệch nhau).
    // Được dùng để so thứ tự các sự kiện do CÙNG một bên phát tạo ra: notification-service so occurredAt
    // của member-added / member-removed (đều do group-service tạo) để bỏ sự kiện cũ đến muộn (Phần 9).
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    // Tên loại sự kiện, ghi vào JSON để người đọc (và Kafka UI) biết đây là sự kiện gì.
    public abstract string EventType { get; }
}
