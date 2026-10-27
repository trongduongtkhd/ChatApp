namespace ChatApp.Contracts.Events;

public record AccountStateChanged(Guid UserId, bool IsLocked, long AuthVersion) : IntegrationEvent
{
    public override string EventType => "AccountStateChanged";
}
