using ChatApp.ChatService.Services;
using ChatApp.Common.Kafka;
using ChatApp.Contracts.Events;
using Microsoft.Extensions.Options;

namespace ChatApp.ChatService.Messaging;

// Nghe group.member-removed (bị xóa, tự rời, hoặc nhóm bị xóa) → xóa cache → người đó không gửi tin được nữa.
// Giới hạn đã biết: nếu người đó đang ở trong phòng SignalR thì vẫn NHẬN tin cho tới khi rời phòng / kết nối lại
// (phòng chỉ kiểm tra quyền lúc JoinGroup).
public sealed class MemberRemovedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<MemberRemovedConsumer> logger)
    : KafkaConsumerBase<MemberRemoved>(scopeFactory, options, logger)
{
    protected override string Topic => KafkaTopics.MemberRemoved;

    protected override Task HandleAsync(MemberRemoved evt, IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<GroupMemberCache>().InvalidateAsync(evt.GroupId);
}
