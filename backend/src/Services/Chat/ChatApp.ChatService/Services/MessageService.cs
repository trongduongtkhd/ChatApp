using ChatApp.ChatService.Data;
using ChatApp.ChatService.Dtos;
using ChatApp.ChatService.Entities;
using ChatApp.Common.Outbox;
using ChatApp.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatApp.ChatService.Services;

public enum SendStatus
{
    // Tin mới, đã lưu → hub phát ReceiveMessage cho cả phòng.
    Created,
    // messageId đã có (client gửi lại) → trả tin cũ, KHÔNG lưu thêm, KHÔNG phát lại, KHÔNG cấp số mới.
    Duplicate,
    // messageId đã bị dùng cho tin của người khác / nhóm khác → từ chối (không trả nội dung tin đó ra).
    IdTaken
}

public record SendMessageResult(SendStatus Status, MessageDto? Message);

// Nghiệp vụ lưu tin nhắn. Hub (ChatHub) lo phần kết nối/phòng/kiểm tra quyền, lớp này lo phần dữ liệu.
public class MessageService(ChatDbContext db, SequenceGenerator sequence)
{
    // Mã lỗi PostgreSQL khi vi phạm unique/primary key.
    private const string UniqueViolation = "23505";
    // Tên PK do EF + snake_case sinh ra (xem migration Initial).
    private const string MessagesPrimaryKey = "pk_messages";
    private const string GroupSequenceIndex = "ix_messages_group_id_sequence_number";
    // Trùng số thứ tự quá 3 lần liên tiếp → có vấn đề nghiêm trọng hơn, ném lỗi ra thay vì lặp mãi.
    private const int MaxSequenceAttempts = 3;
    private const int PreviewLength = 50;

    // DESIGN mục 6, bước 3–5.
    public async Task<SendMessageResult> SendAsync(
        Guid messageId, Guid groupId, Guid senderId, string senderName, string content, CancellationToken ct)
    {
        // IDEMPOTENCY lớp 1: client gửi lại tin đã lưu (vd mất mạng đúng lúc chờ trả lời, tự reconnect rồi gửi lại).
        // Kiểm tra TRƯỚC khi INCR → tin trùng không làm tốn số thứ tự.
        var existing = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (existing is not null)
            return ToDuplicateResult(existing, groupId, senderId);

        for (var attempt = 1; ; attempt++)
        {
            var message = new Message
            {
                Id = messageId,
                GroupId = groupId,
                SenderId = senderId,
                SenderName = senderName,
                Content = content,
                SequenceNumber = await sequence.NextAsync(groupId)
            };
            db.Messages.Add(message);

            // TRANSACTIONAL OUTBOX: sự kiện chat.message-sent được thêm vào CÙNG DbContext,
            // nên SaveChangesAsync bên dưới ghi dòng messages + dòng outbox_messages trong MỘT transaction.
            // Kafka chết lúc này cũng không sao: gửi tin vẫn thành công, OutboxPublisher gửi bù khi Kafka sống lại.
            // Key = groupId → mọi tin của một nhóm vào cùng partition → notification-service đọc đúng thứ tự.
            db.AddOutboxEvent(KafkaTopics.ChatMessageSent, groupId.ToString(), new MessageSent(
                message.Id, groupId, senderId, senderName, message.SequenceNumber, Preview(content)));

            try
            {
                await db.SaveChangesAsync(ct);
                return new SendMessageResult(SendStatus.Created, ToDto(message));
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex, MessagesPrimaryKey))
            {
                // IDEMPOTENCY lớp 2: 2 lần gửi CÙNG messageId đến gần như cùng lúc (vd 2 bản chat-service ở Phần 8):
                // cả hai đều qua bước kiểm tra ở trên (lúc đó chưa ai lưu), nhưng PRIMARY KEY chỉ cho một bên INSERT.
                // Bên thua: transaction hủy → dòng outbox cũng không lưu → notification không đếm 2 lần.
                // Số thứ tự bên thua đã INCR thì bị bỏ phí → dãy seq có "lỗ" (vẫn tăng dần, vẫn không trùng).
                db.ChangeTracker.Clear();
                existing = await db.Messages.AsNoTracking().FirstAsync(m => m.Id == messageId, ct);
                return ToDuplicateResult(existing, groupId, senderId);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex, GroupSequenceIndex) && attempt < MaxSequenceAttempts)
            {
                // Số vừa cấp ĐÃ CÓ tin khác dùng → bộ đếm Redis đang tụt sau DB (vd Redis khôi phục từ bản sao lưu cũ).
                // Unique index chặn lại nên DB không hỏng; ta nâng bộ đếm lên MAX trong DB rồi cấp số mới, thử lại.
                db.ChangeTracker.Clear();
                await sequence.ResyncAsync(groupId, $"seq {message.SequenceNumber} đã được dùng");
            }
        }
    }

    // Lịch sử tin nhắn, phân trang theo KEYSET (DESIGN mục 5): lấy `limit` tin có seq < beforeSeq, mới nhất trước.
    // - beforeSeq = null → trang đầu (tin mới nhất).
    // - Trang sau: client gửi beforeSeq = seq nhỏ nhất của trang vừa nhận. Trả về rỗng = hết tin.
    // Khác OFFSET (bỏ qua N dòng đầu): có tin mới chen vào giữa lúc cuộn thì OFFSET bị lệch (lặp/sót tin),
    // còn "seq < X" luôn đúng chỗ; và DB nhảy thẳng tới X nhờ index (group_id, sequence_number), không phải đếm N dòng.
    public async Task<IReadOnlyList<MessageDto>> GetHistoryAsync(Guid groupId, long? beforeSeq, int limit, CancellationToken ct)
    {
        var query = db.Messages.AsNoTracking().Where(m => m.GroupId == groupId);
        if (beforeSeq is not null)
            query = query.Where(m => m.SequenceNumber < beforeSeq);

        var newestFirst = await query
            .OrderByDescending(m => m.SequenceNumber)
            .Take(limit)
            .ToListAsync(ct);

        // Lấy từ mới về cũ (để LIMIT đúng N tin MỚI NHẤT), trả về theo thứ tự cũ → mới cho client hiển thị.
        return newestFirst.AsEnumerable().Reverse().Select(ToDto).ToList();
    }

    private static bool IsUniqueViolation(DbUpdateException ex, string constraint) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation } pg && pg.ConstraintName == constraint;

    // Chỉ coi là "gửi lại" khi đúng người gửi, đúng nhóm. Nếu không, có thể là kẻ xấu đoán messageId của người khác
    // để đọc trộm nội dung → từ chối, không trả tin.
    private static SendMessageResult ToDuplicateResult(Message existing, Guid groupId, Guid senderId) =>
        existing.GroupId == groupId && existing.SenderId == senderId
            ? new SendMessageResult(SendStatus.Duplicate, ToDto(existing))
            : new SendMessageResult(SendStatus.IdTaken, null);

    // 50 ký tự đầu cho thông báo. Không cắt đôi emoji (emoji là 2 char C# – "surrogate pair").
    private static string Preview(string content)
    {
        if (content.Length <= PreviewLength) return content;
        var length = char.IsHighSurrogate(content[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength;
        return content[..length];
    }

    public static MessageDto ToDto(Message m) =>
        new(m.Id, m.GroupId, m.SenderId, m.SenderName, m.Content, m.SequenceNumber, m.CreatedAt);
}
