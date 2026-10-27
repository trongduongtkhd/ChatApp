using ChatApp.Common.Auth;
using ChatApp.Common.Outbox;
using ChatApp.Contracts.Events;
using ChatApp.IdentityService.Data;
using ChatApp.IdentityService.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.IdentityService.Services;

public static class AdminSeeder
{
    public static async Task RunAsync(IdentityDbContext db, IConfiguration config, ILogger logger)
    {
        var password = config["AdminSeed:Password"];
        if (string.IsNullOrEmpty(password)) { logger.LogInformation("Admin seed: chưa cấu hình AdminSeed:Password; bỏ qua tạo mới."); return; }
        var name = config["AdminSeed:UserName"] ?? "admin";
        var email = config["AdminSeed:Email"] ?? "admin@chatapp.local";
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(908011)");
        var existing = await db.Users.SingleOrDefaultAsync(u => u.UserName == name);
        if (existing is not null)
        {
            if (existing.SystemRole != AccountAccessRules.SystemAdmin)
                throw new InvalidOperationException("Tên seed trùng tài khoản thường; đổi AdminSeed:UserName, không tự nâng quyền.");
            await tx.CommitAsync(); return;
        }
        if (password.Length < 8) throw new InvalidOperationException("AdminSeed:Password cần ít nhất 8 ký tự.");
        var user = new User { UserName = name, Email = email, DisplayName = "Quản trị hệ thống",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password), SystemRole = AccountAccessRules.SystemAdmin };
        db.Users.Add(user);
        db.AddOutboxEvent(KafkaTopics.UserRegistered, user.Id.ToString(), new UserRegistered(user.Id, user.UserName, user.DisplayName));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        logger.LogInformation("Đã seed SystemAdmin {UserName}; mật khẩu không được ghi log.", name);
    }
}
