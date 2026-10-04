using Microsoft.EntityFrameworkCore;

namespace ChatApp.Common.Outbox;

public static class OutboxModelBuilderExtensions
{
    public const string TableName = "outbox_messages";

    // Gọi trong OnModelCreating của DbContext nào cần phát sự kiện (identity, group, chat).
    // Mỗi service có bảng outbox riêng trong DB của mình (database per service).
    // Tên cột snake_case do UseSnakeCaseNamingConvention() của từng service sinh ra.
    public static ModelBuilder AddOutboxMessages(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable(TableName);
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Id).ValueGeneratedNever();
            outbox.Property(m => m.Topic).HasMaxLength(200).IsRequired();
            outbox.Property(m => m.Key).HasMaxLength(200).IsRequired();
            outbox.Property(m => m.EventType).HasMaxLength(100).IsRequired();
            outbox.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();

            // Index một phần: chỉ chứa các dòng CHƯA gửi → publisher tìm nhanh dù bảng có hàng triệu dòng đã gửi.
            outbox.HasIndex(m => new { m.OccurredAt, m.Id })
                .HasFilter("processed_at IS NULL")
                .HasDatabaseName("ix_outbox_messages_unprocessed");
        });
        return modelBuilder;
    }
}
