namespace ChatApp.IdentityService.Entities;

public class User
{
    // GUID v7: 48 bit đầu là thời gian → ID sinh sau lớn hơn, tốt cho index B-tree.
    // Sinh ngay trong ứng dụng, không cần database cấp số.
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string UserName { get; set; } = null!;

    public string Email { get; set; } = null!;

    // Chuỗi BCrypt (đã gồm salt + cost), KHÔNG bao giờ lưu mật khẩu gốc.
    public string PasswordHash { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
