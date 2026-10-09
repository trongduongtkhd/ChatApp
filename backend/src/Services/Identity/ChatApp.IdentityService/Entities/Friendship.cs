using System.ComponentModel.DataAnnotations;

namespace ChatApp.IdentityService.Entities;

// Quan hệ bạn bè 2 chiều lưu MỘT dòng cho mỗi cặp (Phần 11).
// Cặp được sắp bằng DirectChat.Order: UserLowId < UserHighId → "A mời B" và "B mời A" là cùng một dòng,
// PK (user_low_id, user_high_id) chặn 2 lời mời chéo nhau đến cùng lúc (như unique index ở Phần 2, 4).
public class Friendship
{
    public Guid UserLowId { get; set; }

    public Guid UserHighId { get; set; }

    // Người gửi lời mời lần gần nhất (bằng UserLowId hoặc UserHighId – CHECK trong DB).
    public Guid RequesterId { get; set; }

    public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;

    // Số phiên bản của quan hệ (Bước 4b): +1 ở MỖI lần đổi trạng thái (tạo lời mời đầu = 1).
    // Tăng trong cùng SaveChangesAsync có kiểm tra xmin → 2 lần đổi tranh nhau không bao giờ ra cùng một số.
    // Gửi kèm FriendshipChanged → group-service biết sự kiện nào mới hơn mà không cần so giờ hệ thống.
    // (xmin không dùng được thay: là mã giao dịch nội bộ của PostgreSQL, có thể quay vòng, không phải dữ liệu nghiệp vụ.)
    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Lúc người được mời chấp nhận / từ chối; null khi chưa trả lời.
    public DateTimeOffset? RespondedAt { get; set; }

    // Optimistic Locking bằng cột hệ thống xmin (như Group ở group-service):
    // B bấm "Chấp nhận" đúng lúc A bấm "Hủy lời mời" → chỉ một bên ghi được, bên kia 409.
    [Timestamp]
    public uint Version { get; set; }
}

// Máy trạng thái:
//   Pending  → Accepted | Declined | Cancelled
//   Accepted → Removed
//   Declined | Cancelled | Removed → Pending (mời lại, dùng lại dòng cũ)
// Lưu dạng chữ trong DB (dễ đọc khi psql, thêm giá trị mới không lệch số).
public enum FriendshipStatus
{
    Pending,
    Accepted,
    Declined,
    Cancelled,
    Removed
}
