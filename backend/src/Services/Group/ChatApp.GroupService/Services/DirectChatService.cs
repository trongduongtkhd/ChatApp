using ChatApp.Common.Outbox;
using ChatApp.Contracts;
using ChatApp.Contracts.Events;
using ChatApp.GroupService.Data;
using ChatApp.GroupService.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.GroupService.Services;

// Một dòng RETURNING user_id (tên property khớp cột snake_case).
public sealed record AffectedMember(Guid UserId);

// Áp sự kiện identity.friendship-changed lên nhóm chat riêng (Phần 11).
//
// IDEMPOTENT không cần sổ processed_events: mã nhóm TẤT ĐỊNH (DirectChat.GroupIdFor) + ON CONFLICT DO NOTHING.
// Nhận "Accepted" lần 2 → nhóm và 2 thành viên đã có → chèn được 0 dòng → không làm gì, không phát gì.
// Chỉ phát member-added / member-removed cho dòng THẬT SỰ chèn / xóa được (đọc bằng RETURNING),
// nên chat-service và notification-service không nhận sự kiện thừa.
//
// THỨ TỰ: Accepted và Removed cùng topic, cùng key (cặp userId) → đến đúng thứ tự phát.
// Chấp nhận → hủy → chấp nhận lại (kể cả khi group-service tắt lúc đó rồi đọc bù) → kết quả cuối còn 2 thành viên.
//
// SỰ KIỆN CŨ ĐẾN MUỘN (Bước 4b): thứ tự trong partition không chặn được việc một sự kiện CŨ bị giao lại SAU sự kiện mới
// (đẩy tay, lỗi bên phát, gửi lại cả chuỗi). Mỗi sự kiện mang Revision do identity cấp (+1 mỗi lần đổi trạng thái);
// nhóm nhớ friendship_revision đã áp → sự kiện Revision ≤ số đó bị bỏ, không đụng thành viên, không phát gì.
public sealed class DirectChatService(GroupDbContext db, ILogger<DirectChatService> logger)
{
    public async Task ApplyAsync(FriendshipChanged evt, CancellationToken ct)
    {
        if (evt.Change is not (FriendshipChange.Accepted or FriendshipChange.Removed))
        {
            // Loại thay đổi lạ (vd bên phát thêm loại mới sau này): bỏ qua, không ném lỗi
            // (ném lỗi → KafkaConsumerBase thử lại mãi → kẹt cả partition).
            logger.LogWarning("Bỏ qua friendship-changed {EventId}: change '{Change}' không hỗ trợ", evt.EventId, evt.Change);
            return;
        }

        var groupId = DirectChat.GroupIdFor(evt.UserLowId, evt.UserHighId);
        var now = DateTimeOffset.UtcNow;
        // Revision 0 = sự kiện phát trước Bước 4b (JSON chưa có trường này): không so phiên bản, áp như Bước 4.
        int? revision = evt.Revision > 0 ? evt.Revision : null;

        // Kiểm tra phiên bản + nhóm + thành viên + outbox cùng commit hoặc cùng hủy
        // (sập giữa chừng → Kafka giao lại, làm lại từ đầu, friendship_revision chưa bị đổi).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. Nhóm chưa có → tạo luôn với friendship_revision = Revision của sự kiện này (kể cả khi là Removed:
        //    nhóm 0 thành viên nhưng nhớ phiên bản, để một Accepted cũ hơn đến sau vẫn bị bỏ).
        //    Kết bạn lại: nhóm đã có → tin nhắn cũ trong chat_db vẫn gắn với groupId này → lịch sử hiện lại.
        //    Name rỗng: giao diện hiện tên người kia (GroupDto.Peer). OwnerId chỉ để lấp cột, không mang quyền.
        var created = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO groups (id, name, description, owner_id, is_direct, friendship_revision, created_at, updated_at)
            VALUES ({groupId}, '', NULL, {evt.UserLowId}, TRUE, {revision}, {now}, {now})
            ON CONFLICT (id) DO NOTHING
            """, ct);

        // 2. Nhóm đã có → MỘT câu vừa so vừa ghi: chỉ nâng friendship_revision khi sự kiện MỚI HƠN.
        //    UPDATE khóa dòng nhóm tới khi commit → 2 lần xử lý cùng lúc (vd 2 bản group-service) phải xếp hàng,
        //    bản sau thấy số đã được nâng → 0 dòng → bỏ qua. (Đọc rồi mới ghi thì giữa 2 bước có thể bị chen ngang.)
        if (created == 0 && revision is not null)
        {
            var newer = await db.Database.ExecuteSqlAsync($"""
                UPDATE groups SET friendship_revision = {revision}, updated_at = {now}
                WHERE id = {groupId} AND (friendship_revision IS NULL OR friendship_revision < {revision})
                """, ct);
            if (newer == 0)
            {
                var applied = await db.Groups.Where(g => g.Id == groupId).Select(g => g.FriendshipRevision).FirstAsync(ct);
                await tx.CommitAsync(ct);
                logger.LogInformation(
                    "Chat riêng {GroupId}: bỏ qua sự kiện cũ {Change} revision {Revision} (đã áp revision {Applied})",
                    groupId, evt.Change, evt.Revision, applied);
                return;
            }
        }

        if (evt.Change == FriendshipChange.Accepted)
            await AddMembersAsync(groupId, evt, now, ct);
        else
            await RemoveMembersAsync(groupId, evt, ct);

        await tx.CommitAsync(ct);
    }

    private async Task AddMembersAsync(Guid groupId, FriendshipChanged evt, DateTimeOffset now, CancellationToken ct)
    {
        // RETURNING chỉ trả các dòng chèn được (dòng đã có bị ON CONFLICT bỏ qua, không trả về).
        var added = await db.Database.SqlQuery<AffectedMember>($"""
            INSERT INTO group_members (group_id, user_id, role, joined_at)
            VALUES ({groupId}, {evt.UserLowId}, 'Member', {now}),
                   ({groupId}, {evt.UserHighId}, 'Member', {now})
            ON CONFLICT (group_id, user_id) DO NOTHING
            RETURNING user_id
            """).ToListAsync(ct);

        foreach (var m in added)
            db.AddOutboxEvent(KafkaTopics.MemberAdded, groupId.ToString(),
                new MemberAdded(groupId, m.UserId, GroupRole.Member.ToString()));
        await db.SaveChangesAsync(ct);   // chỉ lưu các dòng outbox; dùng transaction đang mở

        logger.LogInformation("Chat riêng {GroupId}: Accepted revision {Revision} (actor {ActorId}) → thêm {Count}/2 thành viên",
            groupId, evt.Revision, evt.ActorId, added.Count);
    }

    private async Task RemoveMembersAsync(Guid groupId, FriendshipChanged evt, CancellationToken ct)
    {
        // Giữ nhóm (và tin nhắn), chỉ gỡ 2 thành viên. Chỉ đụng đúng nhóm có mã DirectChat.GroupIdFor:
        // A và B cùng ở nhóm thường khác thì nhóm đó không bị ảnh hưởng.
        var removed = await db.Database.SqlQuery<AffectedMember>($"""
            DELETE FROM group_members
            WHERE group_id = {groupId} AND user_id IN ({evt.UserLowId}, {evt.UserHighId})
            RETURNING user_id
            """).ToListAsync(ct);

        foreach (var m in removed)
            db.AddOutboxEvent(KafkaTopics.MemberRemoved, groupId.ToString(), new MemberRemoved(groupId, m.UserId));
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Chat riêng {GroupId}: Removed revision {Revision} (actor {ActorId}) → gỡ {Count}/2 thành viên",
            groupId, evt.Revision, evt.ActorId, removed.Count);
    }
}
