using ChatApp.Contracts.Events;
using ChatApp.NotificationService.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.NotificationService.Services;

// Giữ bản sao thành viên nhóm (group_member_snapshots) theo sự kiện của group-service,
// kèm tạo/xóa bộ đếm unread_counters của thành viên đó.
//
// VẤN ĐỀ: added và removed nằm ở 2 topic khác nhau → Kafka KHÔNG bảo đảm thứ tự giữa chúng.
// Cách cũ (Bước 2a: added → INSERT, removed → DELETE) sai khi "xóa Lan" đến trước "thêm Lan":
// DELETE không có gì để xóa, rồi INSERT → Lan ở lại mãi.
//
// CÁCH SỬA (Bước 2b) – tombstone + "chỉ áp sự kiện MỚI HƠN" (last-writer-wins):
// - Xóa KHÔNG xóa dòng mà ghi is_member = false (tombstone), để còn nhớ "đã bị xóa lúc t2".
// - Mỗi dòng nhớ (last_event_at, last_event_id) của sự kiện đã áp. Sự kiện đến sau chỉ được ghi đè
//   nếu (occurredAt, eventId) của nó LỚN HƠN → sự kiện cũ đến muộn bị bỏ qua, thứ tự đến không còn quan trọng.
// occurredAt do group-service gắn lúc tạo sự kiện: added/removed của một nhóm đều do group-service
// (1 bản, cùng một đồng hồ) tạo ra nên so được với nhau. Nếu chạy nhiều bản group-service lệch đồng hồ
// thì cách này có thể sai → hướng tốt hơn là số phiên bản tăng dần do group-service cấp (đồng hồ logic).
public sealed class MemberSnapshotService(NotificationDbContext db, ILogger<MemberSnapshotService> logger)
{
    public Task ApplyAddedAsync(MemberAdded evt, CancellationToken ct) =>
        ApplyAsync(evt, evt.GroupId, evt.UserId, isMember: true, ct);

    public Task ApplyRemovedAsync(MemberRemoved evt, CancellationToken ct) =>
        ApplyAsync(evt, evt.GroupId, evt.UserId, isMember: false, ct);

    private async Task ApplyAsync(IntegrationEvent evt, Guid groupId, Guid userId, bool isMember, CancellationToken ct)
    {
        // Snapshot và bộ đếm cùng đổi hoặc cùng không (một transaction).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Chưa có dòng → INSERT (kể cả khi sự kiện đầu tiên là "xóa": tạo luôn tombstone).
        // Đã có dòng → chỉ UPDATE khi sự kiện này MỚI HƠN sự kiện đã áp (so cặp: thời điểm trước, bằng nhau thì so eventId).
        // Không thỏa WHERE → không đổi gì, trả về 0 dòng.
        // Tự idempotent: nhận lại CHÍNH sự kiện đã áp → cặp bằng nhau, không "lớn hơn" → bỏ qua.
        // 2 consumer cùng ghi một dòng một lúc: PostgreSQL khóa dòng, câu sau chờ câu trước commit
        // rồi xét WHERE trên dữ liệu MỚI NHẤT → không có chuyện ghi đè mất.
        var applied = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO group_member_snapshots AS s (group_id, user_id, is_member, last_event_at, last_event_id)
            VALUES ({groupId}, {userId}, {isMember}, {evt.OccurredAt}, {evt.EventId})
            ON CONFLICT (group_id, user_id) DO UPDATE
            SET is_member = excluded.is_member,
                last_event_at = excluded.last_event_at,
                last_event_id = excluded.last_event_id
            WHERE (s.last_event_at, s.last_event_id) < (excluded.last_event_at, excluded.last_event_id)
            """, ct);

        if (applied == 0)
        {
            await tx.CommitAsync(ct);
            logger.LogInformation(
                "Snapshot: BỎ QUA {EventType} user {UserId} nhóm {GroupId} (occurredAt {OccurredAt:HH:mm:ss.fff}): đã áp sự kiện mới hơn hoặc trùng",
                evt.EventType, userId, groupId, evt.OccurredAt);
            return;
        }

        if (isMember)
        {
            // Thành viên (mới hoặc được thêm lại) bắt đầu với 0 tin chưa đọc. Đã có thì giữ nguyên.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO unread_counters (user_id, group_id, unread_count, last_read_sequence, updated_at)
                VALUES ({userId}, {groupId}, 0, 0, now())
                ON CONFLICT DO NOTHING
                """, ct);
        }
        else
        {
            // Không còn là thành viên → không cần bộ đếm nữa.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM unread_counters
                WHERE group_id = {groupId} AND user_id = {userId}
                """, ct);
        }

        await tx.CommitAsync(ct);
        logger.LogInformation("Snapshot: áp {EventType} user {UserId} nhóm {GroupId} (occurredAt {OccurredAt:HH:mm:ss.fff}) → is_member = {IsMember}",
            evt.EventType, userId, groupId, evt.OccurredAt, isMember);
    }
}
