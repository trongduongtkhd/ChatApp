namespace ChatApp.Contracts.Events;

// Topic identity.friendship-changed, key = DirectChat.PairKey(low, high). identity phát, group nghe.
// "Chấp nhận" và "hủy kết bạn" chung MỘT topic: cùng key → cùng partition → bên nghe nhận đúng thứ tự
// (2 topic riêng thì Kafka không bảo đảm thứ tự giữa chúng, "Removed" có thể đến trước "Accepted").
// ActorId: người bấm chấp nhận / hủy (để ghi log, tra cứu).
// Change: FriendshipChange.Accepted | Removed. Phải là field riêng vì EventType của lớp gốc chỉ là getter,
// JsonSerializer không ghi lại được khi giải mã → bên nghe đọc Change.
// Revision (Bước 4b): friendships.revision sau lần đổi này – số phiên bản do CHÍNH dòng dữ liệu cấp, tăng 1 mỗi lần
// đổi trạng thái. Bên nghe bỏ sự kiện có Revision ≤ bản đã áp → sự kiện cũ đến muộn / gửi lại không ghi đè trạng thái mới.
// Không dùng OccurredAt để so: đồng hồ giữa các bản identity có thể lệch nhau.
// Sự kiện phát trước Bước 4b không có trường này → giải mã ra 0 ("chưa có phiên bản").
public sealed record FriendshipChanged(Guid UserLowId, Guid UserHighId, Guid ActorId, string Change, int Revision) : IntegrationEvent
{
    public override string EventType => Change;
}

public static class FriendshipChange
{
    public const string Accepted = "Accepted";
    public const string Removed = "Removed";
}
