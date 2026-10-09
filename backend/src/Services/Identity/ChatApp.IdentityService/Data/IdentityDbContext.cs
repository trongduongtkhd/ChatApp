using ChatApp.Common.Outbox;
using ChatApp.IdentityService.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.IdentityService.Data;

public class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Friendship> Friendships => Set<Friendship>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.HasKey(u => u.Id);

            // Id do ứng dụng sinh (GUID v7), database không tự sinh.
            user.Property(u => u.Id).ValueGeneratedNever();

            user.Property(u => u.UserName).HasMaxLength(50).IsRequired();
            user.Property(u => u.Email).HasMaxLength(100).IsRequired();
            user.Property(u => u.PasswordHash).IsRequired();
            user.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();

            // Unique index là chốt chặn cuối khi 2 request đăng ký cùng tên đến cùng lúc.
            user.HasIndex(u => u.UserName).IsUnique();
            user.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Friendship>(friendship =>
        {
            // Một dòng cho mỗi cặp: PK là chính cặp userId đã sắp low < high.
            friendship.HasKey(f => new { f.UserLowId, f.UserHighId });

            friendship.Property(f => f.Status).HasConversion<string>().HasMaxLength(20);

            friendship.ToTable(t =>
            {
                // Chốt ở DB, không chỉ tin code: cặp ngược (high, low) hay tự kết bạn (a, a) không chèn được.
                t.HasCheckConstraint("ck_friendships_pair_order", "user_low_id < user_high_id");
                t.HasCheckConstraint("ck_friendships_requester_in_pair", "requester_id IN (user_low_id, user_high_id)");
            });

            // FK sang users ĐƯỢC PHÉP vì cùng identity_db (quy tắc "không FK" chỉ áp cho database khác nhau).
            // Restrict: users không bị xóa; nếu có thì phải dọn quan hệ trước, không lặng lẽ xóa theo.
            friendship.HasOne<User>().WithMany().HasForeignKey(f => f.UserLowId).OnDelete(DeleteBehavior.Restrict);
            friendship.HasOne<User>().WithMany().HasForeignKey(f => f.UserHighId).OnDelete(DeleteBehavior.Restrict);
            friendship.HasOne<User>().WithMany().HasForeignKey(f => f.RequesterId).OnDelete(DeleteBehavior.Restrict);

            // "Quan hệ của tôi" = WHERE user_low_id = @me OR user_high_id = @me.
            // Cột low đã có PK (cột đầu của index kép) → chỉ cần thêm index cho cột high.
            // (EF cũng tự tạo index cho FK requester_id.)
            friendship.HasIndex(f => f.UserHighId);
        });

        // Bảng outbox_messages (cấu hình dùng chung trong ChatApp.Common): sự kiện chờ gửi Kafka.
        modelBuilder.AddOutboxMessages();
    }
}
