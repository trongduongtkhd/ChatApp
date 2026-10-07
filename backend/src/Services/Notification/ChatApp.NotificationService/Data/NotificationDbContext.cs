using ChatApp.NotificationService.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.NotificationService.Data;

// notification-service chỉ NGHE sự kiện, không phát → không có bảng outbox_messages.
public class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<UnreadCounter> UnreadCounters => Set<UnreadCounter>();
    public DbSet<GroupMemberSnapshot> GroupMemberSnapshots => Set<GroupMemberSnapshot>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnreadCounter>(counter =>
        {
            // PK kép: mỗi (user, nhóm) đúng một bộ đếm.
            counter.HasKey(c => new { c.UserId, c.GroupId });
        });

        modelBuilder.Entity<GroupMemberSnapshot>(member =>
        {
            // PK bắt đầu bằng GroupId: câu "lấy mọi thành viên của nhóm G" (Bước 3) dùng được index của PK.
            member.HasKey(m => new { m.GroupId, m.UserId });
        });

        modelBuilder.Entity<ProcessedEvent>(processed =>
        {
            processed.HasKey(p => p.EventId);
            // EventId do bên phát sinh, EF không được tự sinh.
            processed.Property(p => p.EventId).ValueGeneratedNever();
        });
    }
}
