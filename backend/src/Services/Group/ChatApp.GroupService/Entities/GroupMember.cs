namespace ChatApp.GroupService.Entities;

public enum GroupRole
{
    Owner,
    Member
}

public class GroupMember
{
    public Guid GroupId { get; set; }

    // UserId bên identity-service, không có foreign key (khác database).
    public Guid UserId { get; set; }

    public GroupRole Role { get; set; }

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    public Group Group { get; set; } = null!;
}
