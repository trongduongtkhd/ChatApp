using System.ComponentModel.DataAnnotations;

namespace ChatApp.GroupService.Entities;

public class Group
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    // UserId bên identity-service. KHÔNG có foreign key vì users nằm ở database khác.
    // Nhóm chat riêng 2 người (Phần 11): OwnerId = user_low_id chỉ để lấp cột bắt buộc, KHÔNG mang quyền Owner.
    public Guid OwnerId { get; set; }

    // true = chat riêng tạo từ sự kiện kết bạn: Id = DirectChat.GroupIdFor(a, b), 2 thành viên đều Member,
    // không ai sửa / xóa nhóm, thêm / xóa thành viên được (GroupManagementService chặn 400).
    public bool IsDirect { get; set; }

    // Chỉ dùng cho nhóm IsDirect (Bước 4b): Revision của sự kiện friendship-changed đã áp gần nhất.
    // Sự kiện có Revision ≤ số này là sự kiện cũ (gửi lại / đến muộn) → bỏ qua. Nhóm thường: null.
    public int? FriendshipRevision { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Optimistic Locking: Npgsql ánh xạ [Timestamp] uint sang cột hệ thống xmin của PostgreSQL
    // (mã giao dịch đã ghi dòng này lần cuối, tự đổi sau mỗi UPDATE) → không cần tạo cột riêng.
    [Timestamp]
    public uint Version { get; set; }

    public List<GroupMember> Members { get; set; } = [];
}
