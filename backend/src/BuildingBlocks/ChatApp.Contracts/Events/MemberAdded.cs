namespace ChatApp.Contracts.Events;

// Topic group.member-added, key = GroupId. group phát, chat + notification nghe.
// Role: "Owner" hoặc "Member" (dạng chữ, để bên nghe không phụ thuộc enum của group-service).
public sealed record MemberAdded(Guid GroupId, Guid UserId, string Role) : IntegrationEvent
{
    public override string EventType => nameof(MemberAdded);
}
