namespace ChatApp.Contracts.Events;

// Topic identity.user-registered, key = UserId. identity phát, group nghe để cập nhật user_snapshots.
public sealed record UserRegistered(Guid UserId, string UserName, string DisplayName) : IntegrationEvent
{
    public override string EventType => nameof(UserRegistered);
}
