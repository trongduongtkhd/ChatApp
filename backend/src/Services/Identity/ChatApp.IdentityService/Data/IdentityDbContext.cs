using ChatApp.IdentityService.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.IdentityService.Data;

public class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

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
    }
}
