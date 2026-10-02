using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ChatApp.Common.Auth;

// Cấu hình JWT dùng chung cho mọi service (mục "Jwt" trong cấu hình).
// Issuer/Audience/ExpiryMinutes có giá trị mặc định ngay tại đây để các service không bị lệch nhau;
// chỉ Secret là bắt buộc phải cấu hình (user-secrets khi chạy local, biến môi trường Jwt__Secret khi chạy Docker).
public class JwtOptions
{
    public const string SectionName = "Jwt";

    // Ai phát hành token.
    public string Issuer { get; set; } = "chatapp-identity";

    // Token dành cho ai dùng.
    public string Audience { get; set; } = "chatapp";

    public string Secret { get; set; } = "";

    public int ExpiryMinutes { get; set; } = 60;

    public SymmetricSecurityKey GetSigningKey() => new(Encoding.UTF8.GetBytes(Secret));

    // Báo lỗi ngay khi khởi động thay vì để mọi request đều 401 mà không rõ lý do.
    public void Validate()
    {
        if (Encoding.UTF8.GetByteCount(Secret) < 32)
            throw new InvalidOperationException(
                "Thiếu cấu hình Jwt:Secret hoặc secret ngắn hơn 32 byte (HS256 cần khóa >= 256 bit). " +
                "Xem mục 'Secret khi chạy local' trong CLAUDE.md.");
    }
}
