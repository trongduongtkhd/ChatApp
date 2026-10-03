using System.ComponentModel.DataAnnotations;

namespace ChatApp.GroupService.Entities;

public class Group
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    // UserId bên identity-service. KHÔNG có foreign key vì users nằm ở database khác.
    public Guid OwnerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Optimistic Locking: Npgsql ánh xạ [Timestamp] uint sang cột hệ thống xmin của PostgreSQL
    // (mã giao dịch đã ghi dòng này lần cuối, tự đổi sau mỗi UPDATE) → không cần tạo cột riêng.
    [Timestamp]
    public uint Version { get; set; }

    public List<GroupMember> Members { get; set; } = [];
}
