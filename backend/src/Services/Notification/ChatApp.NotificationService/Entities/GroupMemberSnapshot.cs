namespace ChatApp.NotificationService.Entities;

// Bản sao "nhóm G có user U" lấy từ sự kiện group.member-added / group.member-removed.
// Dữ liệu gốc nằm ở group_db (group-service); notification-service giữ bản sao riêng
// để khi có tin mới biết phải tăng số chưa đọc cho ai mà KHÔNG phải gọi sang group-service
// → group-service chết thì notification-service vẫn đếm được (eventual consistency).
//
// added và removed ở 2 topic khác nhau → có thể đến SAI thứ tự. Nên mỗi dòng nhớ thêm
// "sự kiện cuối cùng đã áp là sự kiện nào" và chỉ nhận sự kiện MỚI HƠN (Bước 2b).
public class GroupMemberSnapshot
{
    public Guid GroupId { get; set; }

    public Guid UserId { get; set; }

    // false = TOMBSTONE ("bia mộ"): user đã bị xóa khỏi nhóm. KHÔNG xóa dòng,
    // vì cần giữ lại LastEventAt để chặn một sự kiện "thêm" cũ hơn đến muộn.
    public bool IsMember { get; set; }

    // OccurredAt của sự kiện đã áp gần nhất (thời điểm group-service tạo sự kiện, không phải lúc nhận).
    public DateTimeOffset LastEventAt { get; set; }

    // EventId (GUID v7) của sự kiện đó: phân xử khi 2 sự kiện có cùng OccurredAt.
    public Guid LastEventId { get; set; }
}
