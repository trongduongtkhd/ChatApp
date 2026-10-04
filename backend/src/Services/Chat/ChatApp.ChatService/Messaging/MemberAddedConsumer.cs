using ChatApp.ChatService.Services;
using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using Microsoft.Extensions.Options;

namespace ChatApp.ChatService.Messaging;

// Nghe group.member-added → xóa cache thành viên của nhóm → người mới JoinGroup/gửi tin được.
// Idempotent sẵn: xóa một key 1 lần hay 10 lần kết quả như nhau → không cần bảng processed_events.
public sealed class MemberAddedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<MemberAddedConsumer> logger)
    : KafkaConsumerBase<MemberAdded>(scopeFactory, options, logger)
{
    protected override string Topic => KafkaTopics.MemberAdded;

    protected override Task HandleAsync(MemberAdded evt, IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<GroupMemberCache>().InvalidateAsync(evt.GroupId);
}
