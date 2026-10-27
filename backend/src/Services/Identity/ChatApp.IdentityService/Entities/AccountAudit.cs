namespace ChatApp.IdentityService.Entities;

public class AccountAudit
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ActorId { get; set; }
    public Guid TargetId { get; set; }
    public string Action { get; set; } = null!;
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
