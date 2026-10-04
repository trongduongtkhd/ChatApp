namespace ChatApp.Common.Kafka;

public interface IKafkaProducer
{
    // Gửi một message và CHỜ Kafka xác nhận đã ghi. Lỗi hoặc quá MessageTimeoutMs → ném ProduceException.
    // value là JSON đã chuyển sẵn (lấy từ cột payload của outbox_messages).
    // key quyết định partition: cùng key → cùng partition → giữ đúng thứ tự.
    Task ProduceAsync(string topic, string key, string value, CancellationToken ct);
}
