using ChatApp.Common.Outbox;
using ChatApp.GroupService.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.GroupService.Data;

public class GroupDbContext(DbContextOptions<GroupDbContext> options) : DbContext(options)
{
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupJoinRequest> JoinRequests => Set<GroupJoinRequest>();
    public DbSet<GroupAudit> GroupAudits => Set<GroupAudit>();
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
            group.Property(g => g.InviteTokenHash).HasMaxLength(64);
            group.HasIndex(g => g.InviteTokenHash).IsUnique().HasFilter("invite_token_hash IS NOT NULL");
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
            member.HasIndex(m => m.GroupId).IsUnique().HasFilter("role = 'Owner'").HasDatabaseName("ux_group_single_owner");
            member.ToTable(t => t.HasCheckConstraint("ck_group_members_role", "role IN ('Owner', 'Admin', 'Member')"));

            // Truy vấn "các nhóm tôi tham gia" lọc theo UserId.
            member.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<GroupJoinRequest>(request =>
        {
            request.ToTable("group_join_requests", t => t.HasCheckConstraint("ck_join_request_status", "status IN ('Pending','Approved','Rejected','Cancelled')"));
            request.HasKey(r => r.Id);
            request.Property(r => r.Status).HasMaxLength(20);
            request.HasOne<Group>().WithMany().HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
            request.HasIndex(r => new { r.GroupId, r.UserId }).IsUnique().HasFilter("status = 'Pending'");
            request.HasIndex(r => new { r.GroupId, r.Status, r.CreatedAt });
        });
        modelBuilder.Entity<GroupAudit>(audit =>
        {
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Action).HasMaxLength(50);
            audit.HasIndex(a => new { a.GroupId, a.CreatedAt });
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
