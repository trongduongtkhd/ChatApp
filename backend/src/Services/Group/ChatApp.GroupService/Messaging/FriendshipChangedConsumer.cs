using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using ChatApp.GroupService.Services;
using Microsoft.Extensions.Options;

namespace ChatApp.GroupService.Messaging;

// Nghe identity.friendship-changed → tạo / gỡ thành viên nhóm chat riêng (Phần 11).
// Một partition, consumer group group-service: các sự kiện của mọi cặp được xử lý tuần tự đúng thứ tự phát.
public sealed class FriendshipChangedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<FriendshipChangedConsumer> logger)
    : KafkaConsumerBase<FriendshipChanged>(scopeFactory, options, logger)
{
    protected override string Topic => KafkaTopics.FriendshipChanged;

    protected override Task HandleAsync(FriendshipChanged evt, IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<DirectChatService>().ApplyAsync(evt, ct);
}
