namespace ChatApp.Contracts.Events;

// Topic group.member-removed, key = GroupId. group phát (xóa thành viên hoặc xóa cả nhóm), chat + notification nghe.
public sealed record MemberRemoved(Guid GroupId, Guid UserId) : IntegrationEvent
{
    public override string EventType => nameof(MemberRemoved);
}
