namespace ChatApp.Common.Kafka;

// Cấu hình Kafka dùng chung (mục "Kafka" trong appsettings.json).
// Chạy local: localhost:9092 (listener EXTERNAL). Chạy Docker: ghi đè bằng Kafka__BootstrapServers=kafka:29092.
public class KafkaOptions
{
    public const string SectionName = "Kafka";

    // Danh sách broker để client kết nối lần đầu; sau đó client tự hỏi ra toàn bộ cluster.
    public string BootstrapServers { get; set; } = "";

    // Producer chờ tối đa bao lâu để Kafka xác nhận đã ghi; quá hạn → báo lỗi.
    // Mặc định của librdkafka là 300000 ms (5 phút): Kafka chết thì publisher treo rất lâu mới biết.
    public int MessageTimeoutMs { get; set; } = 5000;

    // Tên consumer group, đặt trùng tên service (group-service, chat-service, notification-service).
    // Kafka nhớ offset đã đọc THEO consumer group → tắt service bật lại vẫn đọc tiếp từ chỗ cũ.
    // Chỉ bắt buộc với service có consumer.
    public string ConsumerGroupId { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BootstrapServers))
            throw new InvalidOperationException(
                "Thiếu cấu hình Kafka:BootstrapServers (vd \"localhost:9092\" trong appsettings.json).");
    }

    public void ValidateConsumer()
    {
        Validate();
        if (string.IsNullOrWhiteSpace(ConsumerGroupId))
            throw new InvalidOperationException(
                "Thiếu cấu hình Kafka:ConsumerGroupId (đặt trùng tên service, vd \"group-service\").");
    }
}
