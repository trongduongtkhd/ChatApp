using System.Text.Json;
using ChatApp.Contracts.Events;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatApp.Common.Kafka;

// Lớp cha cho mọi consumer: một BackgroundService nghe MỘT topic, đọc từng message,
// đổi JSON → TEvent rồi gọi HandleAsync của lớp con. Lớp con chỉ viết nghiệp vụ.
//
// Bảo đảm AT-LEAST-ONCE: xử lý xong mới commit offset.
// Sập giữa "xử lý xong" và "commit" → khởi động lại đọc lại message đó → HandleAsync PHẢI idempotent.
public abstract class KafkaConsumerBase<TEvent>(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger logger) : BackgroundService
    where TEvent : IntegrationEvent
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected abstract string Topic { get; }

    // Xử lý một sự kiện. services: DI scope riêng cho message này (lấy DbContext từ đây).
    // Ném exception → message được thử lại sau 5 giây, KHÔNG bị bỏ qua.
    protected abstract Task HandleAsync(TEvent evt, IServiceProvider services, CancellationToken ct);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Consume() là hàm CHẶN (đứng chờ tới khi có message) → chạy trên luồng riêng của thread pool,
        // không giữ luồng khởi động của ứng dụng.
        Task.Run(() => ConsumeLoopAsync(stoppingToken), stoppingToken);

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        var kafka = options.Value;
        var config = new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            // Các bản cùng một service chung GroupId → Kafka chia partition cho nhau (mỗi message chỉ một bản xử lý).
            // Service khác có GroupId khác → nhận đủ mọi message, độc lập với service này.
            GroupId = kafka.ConsumerGroupId,
            // Consumer group CHƯA từng commit offset nào (lần chạy đầu) → đọc từ đầu topic,
            // nên sự kiện phát ra lúc service này chưa tồn tại / đang tắt vẫn được xử lý bù.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Tắt tự commit theo chu kỳ: tự commit có thể commit message CHƯA xử lý xong → sập là mất.
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetLogHandler((_, log) => logger.Log(KafkaLogging.MapLevel(log.Level), "librdkafka {Facility}: {Message}", log.Facility, log.Message))
            .SetErrorHandler((_, error) =>
            {
                if (error.IsFatal)
                    logger.LogError("Kafka consumer lỗi nghiêm trọng: {Reason}", error.Reason);
                else if (error.Code == ErrorCode.Local_AllBrokersDown)
                    logger.LogWarning("Kafka consumer: không kết nối được broker nào ({Reason})", error.Reason);
            })
            // Kafka chia partition cho các thành viên của group (rebalance) → ghi log để thấy khi chạy 2 bản.
            .SetPartitionsAssignedHandler((_, partitions) =>
                logger.LogInformation("Consumer group {Group} được giao {Topic} partition [{Partitions}]",
                    kafka.ConsumerGroupId, Topic, string.Join(", ", partitions.Select(p => p.Partition.Value))))
            .SetPartitionsRevokedHandler((_, partitions) =>
                logger.LogInformation("Consumer group {Group} trả lại {Topic} partition [{Partitions}]",
                    kafka.ConsumerGroupId, Topic, string.Join(", ", partitions.Select(p => p.Partition.Value))))
            .Build();

        consumer.Subscribe(Topic);
        logger.LogInformation("Consumer {Consumer} bắt đầu nghe {Topic} (group {Group})",
            GetType().Name, Topic, kafka.ConsumerGroupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    // Vd topic chưa tồn tại, broker tạm chết: chờ rồi đọc tiếp.
                    logger.LogWarning("Kafka consume lỗi: {Reason}. Thử lại sau {Delay}s", ex.Error.Reason, RetryDelay.TotalSeconds);
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }

                await ProcessAsync(result, stoppingToken);

                // Ghi nhớ "group này đã xử lý xong tới offset này" lên Kafka (lưu trong topic __consumer_offsets).
                consumer.Commit(result);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service đang tắt.
        }
        finally
        {
            // Báo Kafka "tôi rời group" → Kafka chia lại partition cho bản khác ngay, không phải chờ hết hạn phiên.
            consumer.Close();
        }
    }

    private async Task ProcessAsync(ConsumeResult<string, string> result, CancellationToken stoppingToken)
    {
        TEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<TEvent>(result.Message.Value, KafkaJson.Options);
        }
        catch (JsonException ex)
        {
            // "Poison message": JSON hỏng thì thử lại bao nhiêu lần cũng hỏng → ghi log rồi BỎ QUA,
            // nếu không consumer sẽ kẹt mãi ở message này. (Production: chuyển sang dead-letter topic.)
            logger.LogError(ex, "Bỏ qua message không đọc được ở {Topic} partition {Partition} offset {Offset}",
                result.Topic, result.Partition.Value, result.Offset.Value);
            return;
        }
        if (evt is null)
            return;

        // Lỗi tạm thời (vd PostgreSQL chết): thử lại MÃI message này, không đọc message sau
        // → không mất sự kiện, không đảo thứ tự trong partition.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await HandleAsync(evt, scope.ServiceProvider, stoppingToken);

                logger.LogInformation("Đã xử lý {EventType} {EventId} ({Topic} partition {Partition} offset {Offset})",
                    evt.EventType, evt.EventId, result.Topic, result.Partition.Value, result.Offset.Value);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Xử lý {EventType} {EventId} thất bại (lần {Attempt}), thử lại sau {Delay}s",
                    evt.EventType, evt.EventId, attempt, RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}
