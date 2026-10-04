using ChatApp.Common.Outbox;
using ChatApp.GroupService.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.GroupService.Data;

public class GroupDbContext(DbContextOptions<GroupDbContext> options) : DbContext(options)
{
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<UserSnapshot> UserSnapshots => Set<UserSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Group>(group =>
        {
            group.HasKey(g => g.Id);
            group.Property(g => g.Id).ValueGeneratedNever();
            group.Property(g => g.Name).HasMaxLength(100).IsRequired();
            group.Property(g => g.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<GroupMember>(member =>
        {
            // Một user chỉ xuất hiện một lần trong một nhóm.
            member.HasKey(m => new { m.GroupId, m.UserId });

            // FK tới groups được phép vì cùng database group_db. Xóa nhóm thì xóa luôn thành viên.
            member.HasOne(m => m.Group)
                .WithMany(g => g.Members)
                .HasForeignKey(m => m.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            // Lưu "Owner"/"Member" thay vì 0/1 cho dễ đọc khi xem DB.
            member.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);

            // Truy vấn "các nhóm tôi tham gia" lọc theo UserId.
            member.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<UserSnapshot>(snapshot =>
        {
            snapshot.HasKey(s => s.UserId);
            snapshot.Property(s => s.UserId).ValueGeneratedNever();
            snapshot.Property(s => s.UserName).HasMaxLength(50).IsRequired();
            snapshot.Property(s => s.DisplayName).HasMaxLength(100).IsRequired();
        });

        // Bảng outbox_messages (cấu hình dùng chung trong ChatApp.Common): sự kiện member-added/removed chờ gửi Kafka.
        modelBuilder.AddOutboxMessages();
    }
}
