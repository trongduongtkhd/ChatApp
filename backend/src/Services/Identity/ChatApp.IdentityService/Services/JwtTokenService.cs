using ChatApp.Common.Auth;
using ChatApp.IdentityService.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChatApp.IdentityService.Services;

// Chỉ identity-service TẠO token. Các service khác chỉ KIỂM TRA (JwtAuthenticationExtensions).
public class JwtTokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _jwt = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) CreateToken(User user)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(_jwt.ExpiryMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            // Payload của token: ai cũng đọc được (chỉ Base64), nên KHÔNG để dữ liệu bí mật ở đây.
            Claims = new Dictionary<string, object>
            {
                [ChatAppClaims.UserId] = user.Id.ToString(),
                [ChatAppClaims.UserName] = user.UserName,
                [ChatAppClaims.DisplayName] = user.DisplayName,
                // jti: định danh riêng của từng token.
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
            },
            // Chữ ký HMAC-SHA256 bằng secret chung: sửa 1 ký tự payload là chữ ký không còn khớp.
            SigningCredentials = new SigningCredentials(_jwt.GetSigningKey(), SecurityAlgorithms.HmacSha256)
        };

        return (_handler.CreateToken(descriptor), expiresAt);
    }
}
