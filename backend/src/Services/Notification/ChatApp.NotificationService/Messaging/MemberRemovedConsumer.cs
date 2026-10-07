using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using ChatApp.NotificationService.Services;
using Microsoft.Extensions.Options;

namespace ChatApp.NotificationService.Messaging;

// Nghe group.member-removed (xóa thành viên, tự rời, xóa nhóm → 1 sự kiện cho mỗi thành viên).
// Chạy SONG SONG với MemberAddedConsumer (2 BackgroundService, 2 topic) → không có thứ tự giữa hai bên.
public sealed class MemberRemovedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<MemberRemovedConsumer> logger)
    : KafkaConsumerBase<MemberRemoved>(scopeFactory, options, logger)
{
    protected override string Topic => KafkaTopics.MemberRemoved;

    protected override Task HandleAsync(MemberRemoved evt, IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<MemberSnapshotService>().ApplyRemovedAsync(evt, ct);
}
