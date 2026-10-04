using ChatApp.ChatService.Entities;
using ChatApp.Common.Outbox;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.ChatService.Data;

public class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Message>(message =>
        {
            message.HasKey(m => m.Id);
            // Id do client gửi lên, EF không được tự sinh.
            message.Property(m => m.Id).ValueGeneratedNever();
            message.Property(m => m.SenderName).HasMaxLength(100).IsRequired();
            message.Property(m => m.Content).IsRequired();

            // Chốt chặn cuối cùng: trong một nhóm không thể có 2 tin cùng số thứ tự,
            // kể cả khi bộ đếm Redis bị mất/khởi tạo sai (Bước 4).
            // Index này cũng phục vụ truy vấn lịch sử "WHERE group_id = ? AND sequence_number < ? ORDER BY sequence_number".
            message.HasIndex(m => new { m.GroupId, m.SequenceNumber }).IsUnique();
        });

        // Bảng outbox_messages (cấu hình dùng chung trong ChatApp.Common): sự kiện chat.message-sent chờ gửi Kafka.
        modelBuilder.AddOutboxMessages();
    }
}
