using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatApp.Common.Kafka;

// Đăng ký Singleton: cả service dùng chung MỘT producer.
// Producer giữ kết nối TCP tới broker và một hàng đợi gửi nền; tạo mới cho mỗi lần gửi là rất tốn.
public sealed class KafkaProducer : IKafkaProducer, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaProducer> _logger;

    public KafkaProducer(IOptions<KafkaOptions> options, IHostEnvironment env, ILogger<KafkaProducer> logger)
    {
        _logger = logger;
        var kafka = options.Value;

        var config = new ProducerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            // Tên client hiện trong log của Kafka, biết message do service nào gửi.
            ClientId = env.ApplicationName,
            // Chỉ coi là gửi xong khi broker đã GHI message (với cluster nhiều broker: mọi bản sao đồng bộ đã ghi).
            Acks = Acks.All,
            // Producer tự gửi lại khi lỗi mạng; bật idempotence để broker loại bản gửi lại bị trùng
            // (mỗi message mang mã producer + số thứ tự, broker thấy số đã ghi rồi thì bỏ qua).
            EnableIdempotence = true,
            MessageTimeoutMs = kafka.MessageTimeoutMs
        };

        _producer = new ProducerBuilder<string, string>(config)
            // Log nội bộ của librdkafka (mặc định in thẳng ra stderr) → chuyển vào ILogger cho thống nhất.
            .SetLogHandler((_, log) => _logger.Log(KafkaLogging.MapLevel(log.Level), "librdkafka {Facility}: {Message}", log.Facility, log.Message))
            // librdkafka tự kết nối lại khi broker chết; ở đây chỉ ghi log để thấy chuyện gì đang xảy ra.
            .SetErrorHandler((_, error) =>
            {
                // Lỗi kết nối từng broker đã có trong log handler ở trên → chỉ ghi thêm lỗi nghiêm trọng
                // và trạng thái "toàn bộ broker đều chết".
                if (error.IsFatal)
                    _logger.LogError("Kafka producer lỗi nghiêm trọng: {Reason}", error.Reason);
                else if (error.Code == ErrorCode.Local_AllBrokersDown)
                    _logger.LogWarning("Kafka producer: không kết nối được broker nào ({Reason})", error.Reason);
            })
            .Build();
    }

    public async Task ProduceAsync(string topic, string key, string value, CancellationToken ct)
    {
        var result = await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value }, ct);

        // Partition + offset = "địa chỉ" của message trong Kafka; xem lại được ở Kafka UI.
        _logger.LogInformation("Đã gửi Kafka {Topic} key={Key} → partition {Partition}, offset {Offset}",
            result.Topic, key, result.Partition.Value, result.Offset.Value);
    }

    public void Dispose()
    {
        // Khi tắt service: chờ tối đa 5 giây để gửi nốt message còn trong hàng đợi nội bộ.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
