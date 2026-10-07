using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using ChatApp.NotificationService.Services;
using Microsoft.Extensions.Options;

namespace ChatApp.NotificationService.Messaging;

// Nghe group.member-added (consumer group "notification-service", độc lập với "chat-service":
// cả 2 service đều nhận ĐỦ mọi sự kiện, mỗi bên giữ offset riêng).
public sealed class MemberAddedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<MemberAddedConsumer> logger)
    : KafkaConsumerBase<MemberAdded>(scopeFactory, options, logger)
{
    protected override string Topic => KafkaTopics.MemberAdded;

    protected override Task HandleAsync(MemberAdded evt, IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<MemberSnapshotService>().ApplyAddedAsync(evt, ct);
}
