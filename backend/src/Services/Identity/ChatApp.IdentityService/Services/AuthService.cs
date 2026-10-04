using ChatApp.Common.Outbox;
using ChatApp.Contracts.Events;
using ChatApp.IdentityService.Data;
using ChatApp.IdentityService.Dtos;
using ChatApp.IdentityService.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatApp.IdentityService.Services;

public record RegisterResult(UserDto? User, string? Error);

public class AuthService(IdentityDbContext db, JwtTokenService jwtTokenService)
{
    // Mã lỗi PostgreSQL khi vi phạm unique constraint.
    private const string UniqueViolation = "23505";

    // Hash giả để vẫn chạy BCrypt.Verify khi username không tồn tại:
    // thời gian trả lời giống nhau → kẻ xấu không đo thời gian để dò username.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-password");

    public async Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var userName = request.UserName.Trim();
        var email = request.Email.Trim().ToLowerInvariant();

        // Kiểm tra trước để trả thông báo dễ hiểu cho trường hợp thường gặp.
        if (await db.Users.AnyAsync(u => u.UserName == userName, ct))
            return new RegisterResult(null, "UserName đã tồn tại");
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return new RegisterResult(null, "Email đã tồn tại");

        var user = new User
        {
            UserName = userName,
            Email = email,
            // BCrypt tự sinh salt ngẫu nhiên và nhúng salt + cost vào chuỗi kết quả.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            DisplayName = request.DisplayName.Trim()
        };

        db.Users.Add(user);

        // Transactional Outbox: KHÔNG gửi Kafka ở đây. Chỉ thêm dòng outbox_messages,
        // rồi SaveChangesAsync lưu INSERT users + INSERT outbox_messages trong CÙNG một transaction.
        // OutboxPublisher (chạy nền) sẽ gửi lên Kafka sau; Kafka có chết thì sự kiện vẫn nằm chờ trong DB.
        db.AddOutboxEvent(KafkaTopics.UserRegistered, user.Id.ToString(),
            new UserRegistered(user.Id, user.UserName, user.DisplayName));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Hai request cùng tên đến cùng lúc: cả hai đều qua bước AnyAsync ở trên,
            // nhưng unique index chỉ cho một cái INSERT thành công.
            // Transaction bị hủy → dòng outbox cũng không được lưu → không phát sự kiện cho user không tồn tại.
            return new RegisterResult(null, "UserName hoặc Email đã tồn tại");
        }

        return new RegisterResult(ToDto(user), null);
    }

    // Trả null nếu sai username HOẶC sai mật khẩu (không nói rõ cái nào sai).
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var userName = request.UserName.Trim();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserName == userName, ct);

        // BCrypt đọc salt + cost từ chuỗi hash đã lưu, băm lại mật khẩu nhập vào rồi so sánh.
        var passwordOk = BCrypt.Net.BCrypt.Verify(request.Password, user?.PasswordHash ?? DummyHash);
        if (user is null || !passwordOk)
            return null;

        var (token, expiresAt) = jwtTokenService.CreateToken(user);
        return new LoginResponse(token, expiresAt);
    }

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? null : ToDto(user);
    }

    private static UserDto ToDto(User user) =>
        new(user.Id, user.UserName, user.Email, user.DisplayName, user.CreatedAt);
}
