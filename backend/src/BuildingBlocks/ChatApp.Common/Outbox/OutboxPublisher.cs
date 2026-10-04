using ChatApp.Common.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatApp.Common.Outbox;

// Tiến trình nền (BackgroundService) chạy suốt đời service, song song với các request HTTP:
// đọc outbox_messages chưa gửi → gửi Kafka → đánh dấu đã gửi.
// Generic theo TDbContext để identity, group, chat dùng chung một code với DbContext của mình.


// chay nen lấy sự kiện từ bảng outbox_messages rồi gửi lên Kafka. Nó đứng giữa database và Kafka.
// sealed : nghia là không thể kế thừa lớp này nữa, tránh bị override các phương thức quan trọng.
public sealed class OutboxPublisher<TDbContext>(
    IServiceScopeFactory scopeFactory,
    IKafkaProducer producer,
    ILogger<OutboxPublisher<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private const int BatchSize = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox publisher ({DbContext}) bắt đầu chạy", typeof(TDbContext).Name);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var fullBatch = false;
                try
                {
                    fullBatch = await PublishBatchAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Lỗi DB (vd PostgreSQL tạm chết): ghi log rồi thử lại vòng sau, không để tiến trình nền chết.
                    logger.LogError(ex, "Outbox publisher lỗi, sẽ thử lại");
                }

                // Lô đầy → có thể còn nữa, chạy tiếp ngay. Không thì chờ 1 giây rồi hỏi lại DB (polling).
                if (!fullBatch)
                    await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service đang tắt: thoát vòng lặp bình thường.
        }
    }

    // Trả true nếu đã gửi đủ một lô đầy (khả năng còn dòng chưa gửi).
    private async Task<bool> PublishBatchAsync(CancellationToken ct)
    {
        // BackgroundService là Singleton, DbContext là Scoped → tự tạo scope cho mỗi lô.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // FOR UPDATE: khóa các dòng vừa đọc tới khi transaction kết thúc.
        // SKIP LOCKED: dòng đang bị bản khác khóa thì BỎ QUA thay vì đứng chờ.
        // → Khi chạy 2 bản service (Phần 8), mỗi dòng chỉ một bản lấy được, không gửi đôi.
        var batch = await db.Set<OutboxMessage>()
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL
                ORDER BY occurred_at, id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        if (batch.Count == 0)
            return false;

        var sent = 0;
        foreach (var message in batch)
        {
            message.Attempts++;
            try
            {
                // Chờ Kafka xác nhận đã ghi (Acks.All) rồi mới đánh dấu ProcessedAt.
                await producer.ProduceAsync(message.Topic, message.Key, message.Payload, ct);
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.LastError = ex.Message;
                logger.LogWarning("Outbox: gửi {EventType} {EventId} thất bại (lần {Attempts}): {Error}. Sẽ thử lại sau",
                    message.EventType, message.Id, message.Attempts, ex.Message);
                // Dừng cả lô: nếu gửi tiếp các dòng sau thì sự kiện mới lên Kafka TRƯỚC sự kiện cũ → sai thứ tự
                // (vd member-removed tới trước member-added của cùng một người).
                break;
            }
        }

        // Lưu kết quả kể cả khi service đang tắt (CancellationToken.None), để không gửi lại các dòng đã gửi xong.
        // Nếu sập ĐÚNG giữa "Kafka đã ghi" và "commit ProcessedAt" → lần sau gửi lại → trùng (at-least-once).
        await db.SaveChangesAsync(CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);

        if (sent > 0)
            logger.LogInformation("Outbox: đã gửi {Sent}/{Total} sự kiện", sent, batch.Count);

        return sent == BatchSize;
    }
}
