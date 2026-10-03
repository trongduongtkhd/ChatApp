namespace ChatApp.GroupService.Entities;

// Bản sao (chỉ các field cần dùng) của user bên identity-service.
// identity là chủ dữ liệu; group-service cập nhật bản sao này từ sự kiện identity.user-registered (Phần 5)
// → nhất quán dần dần (eventual consistency), không gọi sang identity mỗi lần cần tên.
public class UserSnapshot
{
    public Guid UserId { get; set; }

    public string UserName { get; set; } = null!;

    public string DisplayName { get; set; } = null!;
}
