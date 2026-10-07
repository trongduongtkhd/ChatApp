using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using ChatApp.NotificationService.Services;
using Microsoft.Extensions.Options;

namespace ChatApp.NotificationService.Messaging;

// Nghe chat.message-sent (3 partition, key = groupId → tin cùng nhóm luôn cùng partition, đúng thứ tự seq).
// Chạy 1 bản → consumer này được giao CẢ 3 partition. Chạy 2 bản cùng group "notification-service"
// thì Kafka chia partition cho nhau (vd 2 + 1), mỗi tin vẫn chỉ một bản xử lý.
public sealed class MessageSentConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<MessageSentConsumer> logger)
    : KafkaConsumerBase<MessageSent>(scopeFactory, options, logger)
{
    protected override string Topic => KafkaTopics.ChatMessageSent;

    protected override async Task HandleAsync(MessageSent evt, IServiceProvider services, CancellationToken ct)
    {
        // 1. Cập nhật DB (transaction đã commit khi hàm trả về). Sự kiện trùng → danh sách rỗng → không đẩy gì.
        var changes = await services.GetRequiredService<UnreadCounterService>().ApplyMessageSentAsync(evt, ct);
        // 2. Rồi mới đẩy số mới tới trình duyệt của từng người nhận.
        await services.GetRequiredService<UnreadNotifier>().NotifyAsync(evt.GroupId, changes);
    }
}
