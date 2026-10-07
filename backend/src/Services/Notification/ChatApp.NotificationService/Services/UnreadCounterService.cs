using ChatApp.Contracts.Events;
using ChatApp.NotificationService.Data;
using ChatApp.NotificationService.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.NotificationService.Services;

// Một người nhận có bộ đếm vừa thay đổi (Bước 5 dùng để đẩy UnreadCountChanged).
// Tên property khớp tên cột (snake_case) mà câu SQL RETURNING trả về.
public sealed record UnreadChange(Guid UserId, int UnreadCount);

// Tăng số tin chưa đọc khi có tin mới (chat.message-sent).
//
// IDEMPOTENT CONSUMER: Kafka giao at-least-once (outbox gửi lại, consumer sập trước khi commit offset...)
// → cùng một MessageSent có thể đến 2 lần. "unread_count + 1" chạy 2 lần là SAI (khác upsert/xóa cache ở
// Phần 5, 7 và bản sao thành viên ở Bước 2b: chạy lại vẫn ra cùng kết quả).
// Cách làm: ghi eventId vào processed_events TRONG CÙNG transaction với việc tăng bộ đếm.
// - Lần đầu: INSERT được → tăng bộ đếm → COMMIT cả hai.
// - Lần sau: INSERT đụng PK → 0 dòng → bỏ qua.
// - Sập giữa chừng: transaction hủy cả hai → lần sau làm lại từ đầu như lần đầu.
// → Không bao giờ có "đã tăng nhưng chưa ghi sổ" hay "đã ghi sổ nhưng chưa tăng".
public sealed class UnreadCounterService(
    NotificationDbContext db,
    IConfiguration configuration,
    ILogger<UnreadCounterService> logger)
{
    // Công tắc CHỈ để demo (giống SignalR:RedisBackplane của chat-service): false → không ghi/kiểm tra processed_events
    // → sự kiện trùng bị đếm thêm lần nữa. Chạy thật luôn true.
    private readonly bool _idempotent = configuration.GetValue("Notification:IdempotentConsumer", true);

    // Trả về các người nhận có bộ đếm thay đổi; rỗng nếu sự kiện trùng.
    public async Task<IReadOnlyList<UnreadChange>> ApplyMessageSentAsync(MessageSent evt, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. Ghi sổ. PK event_id chặn cả khi 2 lần xử lý cùng sự kiện chạy ĐỒNG THỜI (vd 2 bản service):
        //    bản sau phải chờ bản trước commit, rồi thấy trùng → 0 dòng.
        var firstTime = !_idempotent || await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO processed_events (event_id, processed_at)
            VALUES ({evt.EventId}, now())
            ON CONFLICT DO NOTHING
            """, ct) == 1;

        if (!firstTime)
        {
            await tx.CommitAsync(ct);
            logger.LogWarning("Bỏ qua sự kiện trùng {EventId} (MessageSent seq {Seq} nhóm {GroupId}): đã xử lý rồi",
                evt.EventId, evt.SequenceNumber, evt.GroupId);
            return [];
        }

        // 2. +1 cho mọi thành viên (theo bản sao, is_member) TRỪ người gửi, trong MỘT câu lệnh.
        //    Chỉ tăng khi tin này mới hơn mốc đã đọc: tin đến muộn mà người nhận đã đọc tới đó rồi thì không đếm.
        //    Mỗi thành viên luôn có sẵn một dòng unread_counters (tạo cùng transaction khi áp member-added, Bước 2)
        //    → chỉ cần UPDATE. Thành viên mà member-added CHƯA tới (2 topic khác nhau) thì chưa có trong bản sao
        //    → tin này không được đếm cho họ (eventual consistency, ghi chú báo cáo).
        var changes = await db.Database.SqlQuery<UnreadChange>($"""
            UPDATE unread_counters c
            SET unread_count = c.unread_count + 1, updated_at = now()
            FROM group_member_snapshots s
            WHERE s.group_id = {evt.GroupId} AND s.is_member AND s.user_id <> {evt.SenderId}
              AND c.group_id = s.group_id AND c.user_id = s.user_id
              AND c.last_read_sequence < {evt.SequenceNumber}
            RETURNING c.user_id, c.unread_count
            """).ToListAsync(ct);

        // 3. Người gửi: đang ở trong nhóm và gửi tin → coi như đã đọc tới tin của chính mình.
        //    Cùng quy tắc với POST .../read (MarkReadAsync): nâng mốc, về 0 nếu vượt mốc cũ.
        //    (Chỉ nâng mốc mà không về 0 thì các tin chưa đọc nằm DƯỚI mốc mới sẽ không bao giờ xóa được.)
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE unread_counters c
            SET last_read_sequence = GREATEST(c.last_read_sequence, {evt.SequenceNumber}),
                unread_count = CASE WHEN {evt.SequenceNumber} > c.last_read_sequence THEN 0 ELSE c.unread_count END,
                updated_at = now()
            WHERE c.group_id = {evt.GroupId} AND c.user_id = {evt.SenderId}
            """, ct);

        await tx.CommitAsync(ct);
        logger.LogInformation("MessageSent seq {Seq} nhóm {GroupId} từ {Sender}: +1 cho {Count} người",
            evt.SequenceNumber, evt.GroupId, evt.SenderName, changes.Count);
        return changes;
    }

    // Mọi nhóm user đang là thành viên (mỗi thành viên luôn có đúng một dòng bộ đếm, kể cả 0).
    public async Task<IReadOnlyList<UnreadCounterDto>> GetForUserAsync(Guid userId, CancellationToken ct) =>
        await db.UnreadCounters
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new UnreadCounterDto(c.GroupId, c.UnreadCount, c.LastReadSequence))
            .ToListAsync(ct);

    // Đánh dấu đã đọc tới tin lastReadSequence. null = user không phải thành viên (không có bộ đếm) → 403.
    public async Task<UnreadCounterDto?> MarkReadAsync(Guid userId, Guid groupId, long lastReadSequence, CancellationToken ct)
    {
        // MỘT câu UPDATE, không đọc trước rồi mới ghi → không bị chen giữa bởi consumer đang +1 cùng dòng
        // (PostgreSQL khóa dòng; câu nào đến sau chờ câu trước commit rồi tính trên giá trị mới nhất).
        // Trong SET, c.last_read_sequence luôn là giá trị CŨ (trước khi cập nhật):
        // - Mốc chỉ tăng (GREATEST): request cũ đến muộn (2 tab, mạng chậm) không kéo mốc lùi.
        // - Chỉ về 0 khi request VƯỢT mốc đang lưu (>, không phải >=). Mọi tin được đếm đều có seq > mốc
        //   lúc được đếm; request bằng/thấp hơn mốc thì chưa chắc đã thấy các tin đó → giữ nguyên số đếm.
        //   Vd mốc 3 (tin tự gửi), đếm 1 (seq 4 của người khác), tab cũ gửi read 3 → vẫn 1, đúng.
        // Không còn là thành viên → không có dòng (xóa khi áp member-removed) → 0 dòng → 403.
        var rows = await db.Database.SqlQuery<UnreadCounterDto>($"""
            UPDATE unread_counters c
            SET last_read_sequence = GREATEST(c.last_read_sequence, {lastReadSequence}),
                unread_count = CASE WHEN {lastReadSequence} > c.last_read_sequence THEN 0 ELSE c.unread_count END,
                updated_at = now()
            WHERE c.user_id = {userId} AND c.group_id = {groupId}
            RETURNING c.group_id, c.unread_count, c.last_read_sequence
            """).ToListAsync(ct);

        var result = rows.SingleOrDefault();
        if (result is not null)
            logger.LogInformation("User {UserId} đọc nhóm {GroupId} tới seq {Seq} → unread {Unread}, mốc {Mark}",
                userId, groupId, lastReadSequence, result.UnreadCount, result.LastReadSequence);
        return result;
    }
}
