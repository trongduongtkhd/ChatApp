using ChatApp.Common.Outbox;
using ChatApp.Contracts;
using ChatApp.Contracts.Events;
using ChatApp.IdentityService.Data;
using ChatApp.IdentityService.Dtos;
using ChatApp.IdentityService.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatApp.IdentityService.Services;

// Bạn bè (Phần 11). Một dòng friendships cho mỗi cặp, đi theo máy trạng thái:
//   Pending  → Accepted | Declined | Cancelled
//   Accepted → Removed
//   Declined | Cancelled | Removed → Pending (mời lại)
// Mỗi thao tác: đọc dòng → kiểm tra trạng thái + vai trò → đổi → SaveChangesAsync kèm xmin.
// Hai thao tác tranh nhau trên cùng dòng (B chấp nhận đúng lúc A hủy) → bên ghi sau DbUpdateConcurrencyException → 409.
public class FriendshipService(IdentityDbContext db)
{
    private const string UniqueViolation = "23505";

    public async Task<IReadOnlyList<FriendDto>> ListFriendsAsync(Guid me, CancellationToken ct)
    {
        var rows = await (
                from f in db.Friendships
                where (f.UserLowId == me || f.UserHighId == me) && f.Status == FriendshipStatus.Accepted
                join u in db.Users on (f.UserLowId == me ? f.UserHighId : f.UserLowId) equals u.Id
                orderby u.DisplayName
                select new { u.Id, u.UserName, u.DisplayName, Since = f.RespondedAt ?? f.UpdatedAt })
            .AsNoTracking()
            .ToListAsync(ct);

        // Mã nhóm chat riêng tính tại chỗ: cùng công thức với group-service (ChatApp.Contracts.DirectChat).
        return rows
            .Select(r => new FriendDto(r.Id, r.UserName, r.DisplayName, r.Since, DirectChat.GroupIdFor(me, r.Id)))
            .ToList();
    }

    // Lời mời ĐẾN: đang Pending, người mời là người kia.
    public Task<List<FriendRequestDto>> ListIncomingAsync(Guid me, CancellationToken ct) =>
        (from f in db.Friendships
         where (f.UserLowId == me || f.UserHighId == me) && f.Status == FriendshipStatus.Pending && f.RequesterId != me
         join u in db.Users on f.RequesterId equals u.Id
         orderby f.UpdatedAt descending
         select new FriendRequestDto(u.Id, u.UserName, u.DisplayName, f.UpdatedAt))
        .AsNoTracking()
        .ToListAsync(ct);

    // Lời mời ĐÃ GỬI: đang Pending, người mời là mình.
    public Task<List<FriendRequestDto>> ListOutgoingAsync(Guid me, CancellationToken ct) =>
        (from f in db.Friendships
         where f.RequesterId == me && f.Status == FriendshipStatus.Pending
         join u in db.Users on (f.UserLowId == me ? f.UserHighId : f.UserLowId) equals u.Id
         orderby f.UpdatedAt descending
         select new FriendRequestDto(u.Id, u.UserName, u.DisplayName, f.UpdatedAt))
        .AsNoTracking()
        .ToListAsync(ct);

    public async Task<ServiceResult<FriendRequestDto>> SendRequestAsync(Guid me, Guid otherId, CancellationToken ct)
    {
        if (otherId == me)
            return ServiceResult<FriendRequestDto>.Fail(ServiceError.BadRequest, "Không thể kết bạn với chính mình");

        var other = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == otherId, ct);
        if (other is null)
            return ServiceResult<FriendRequestDto>.Fail(ServiceError.NotFound, "Không tìm thấy người dùng");

        var friendship = await FindAsync(me, otherId, ct);
        var now = DateTimeOffset.UtcNow;

        if (friendship is null)
        {
            var (low, high) = DirectChat.Order(me, otherId);
            // Lời mời đầu tiên của cặp: trạng thái Pending, Revision = 1.
            friendship = new Friendship { UserLowId = low, UserHighId = high, RequesterId = me, Revision = 1, CreatedAt = now, UpdatedAt = now };
            db.Friendships.Add(friendship);
        }
        else
        {
            if (friendship.Status is FriendshipStatus.Accepted or FriendshipStatus.Pending)
                return ServiceResult<FriendRequestDto>.Fail(ServiceError.Conflict, DescribeBlocking(friendship, me));

            // Declined | Cancelled | Removed → Pending: mời lại, dùng lại dòng cũ, người mời là mình.
            ChangeStatus(friendship, FriendshipStatus.Pending, now);
            friendship.RequesterId = me;
            friendship.RespondedAt = null;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // A mời B và B mời A đến cùng lúc: cả hai đều thấy "chưa có dòng", nhưng PK (low, high) chỉ cho
            // một INSERT thành công. Bên thua đọc lại dòng của bên thắng để báo đúng lý do.
            db.ChangeTracker.Clear();
            var winner = await FindAsync(me, otherId, ct);
            return ServiceResult<FriendRequestDto>.Fail(ServiceError.Conflict,
                winner is null ? "Lời mời vừa bị thay đổi, hãy thử lại" : DescribeBlocking(winner, me));
        }
        catch (DbUpdateConcurrencyException)
        {
            // Mời lại đúng lúc người kia cũng mời lại / thay đổi dòng: xmin đã đổi → 0 dòng được cập nhật.
            return ServiceResult<FriendRequestDto>.Fail(ServiceError.Conflict, "Lời mời vừa bị thay đổi, hãy tải lại");
        }

        return ServiceResult<FriendRequestDto>.Ok(new FriendRequestDto(other.Id, other.UserName, other.DisplayName, now));
    }

    public async Task<ServiceResult<FriendDto>> AcceptAsync(Guid me, Guid otherId, CancellationToken ct)
    {
        if (otherId == me)
            return ServiceResult<FriendDto>.Fail(ServiceError.BadRequest, "Không thể kết bạn với chính mình");

        var friendship = await FindAsync(me, otherId, ct);

        // Đã là bạn (bấm 2 lần, hoặc gửi lại request sau khi mất kết nối) → trả kết quả như lần đầu, không làm gì thêm.
        if (friendship is { Status: FriendshipStatus.Accepted })
            return await FriendResultAsync(me, otherId, friendship, ct);

        if (friendship is not { Status: FriendshipStatus.Pending })
            return ServiceResult<FriendDto>.Fail(ServiceError.NotFound, "Không có lời mời kết bạn từ người này");
        if (friendship.RequesterId == me)
            return ServiceResult<FriendDto>.Fail(ServiceError.Forbidden, "Bạn là người gửi lời mời, chỉ người được mời mới chấp nhận được");

        var now = DateTimeOffset.UtcNow;
        ChangeStatus(friendship, FriendshipStatus.Accepted, now);
        friendship.RespondedAt = now;
        // Transactional Outbox: UPDATE friendships + INSERT outbox_messages trong CÙNG một SaveChangesAsync.
        // Thua xmin (catch bên dưới) → cả transaction bị hủy → dòng outbox cũng không lưu
        // → bấm chấp nhận 2 lần chỉ phát MỘT sự kiện.
        AddFriendshipChangedEvent(friendship, me, FriendshipChange.Accepted);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Dòng đã bị đổi giữa lúc đọc và lúc ghi. Đọc lại để phân biệt:
            // - một request "chấp nhận" khác của chính mình đã thắng → đã là bạn → 200 như trên;
            // - người kia vừa hủy lời mời → 409.
            db.ChangeTracker.Clear();
            var current = await FindAsync(me, otherId, ct);
            if (current is { Status: FriendshipStatus.Accepted })
                return await FriendResultAsync(me, otherId, current, ct);
            return ServiceResult<FriendDto>.Fail(ServiceError.Conflict, "Lời mời vừa bị thay đổi (có thể đã bị hủy), hãy tải lại");
        }

        return await FriendResultAsync(me, otherId, friendship, ct);
    }

    public async Task<ServiceResult> DeclineAsync(Guid me, Guid otherId, CancellationToken ct)
    {
        var friendship = otherId == me ? null : await FindAsync(me, otherId, ct);
        if (friendship is not { Status: FriendshipStatus.Pending })
            return ServiceResult.Fail(ServiceError.NotFound, "Không có lời mời kết bạn từ người này");
        if (friendship.RequesterId == me)
            return ServiceResult.Fail(ServiceError.Forbidden, "Bạn là người gửi lời mời, hãy dùng Hủy lời mời");

        var now = DateTimeOffset.UtcNow;
        ChangeStatus(friendship, FriendshipStatus.Declined, now);
        friendship.RespondedAt = now;
        return await SaveOrConflictAsync(ct);
    }

    public async Task<ServiceResult> CancelAsync(Guid me, Guid otherId, CancellationToken ct)
    {
        var friendship = otherId == me ? null : await FindAsync(me, otherId, ct);
        if (friendship is not { Status: FriendshipStatus.Pending })
            return ServiceResult.Fail(ServiceError.NotFound, "Không có lời mời đang chờ với người này");
        if (friendship.RequesterId != me)
            return ServiceResult.Fail(ServiceError.Forbidden, "Người này mời bạn, hãy dùng Từ chối");

        ChangeStatus(friendship, FriendshipStatus.Cancelled, DateTimeOffset.UtcNow);
        return await SaveOrConflictAsync(ct);
    }

    public async Task<ServiceResult> RemoveFriendAsync(Guid me, Guid otherId, CancellationToken ct)
    {
        var friendship = otherId == me ? null : await FindAsync(me, otherId, ct);
        if (friendship is not { Status: FriendshipStatus.Accepted })
            return ServiceResult.Fail(ServiceError.NotFound, "Hai bạn không phải bạn bè");

        // Accepted → Removed: giữ dòng (không DELETE) để mời lại sau này dùng lại đúng dòng/cặp.
        ChangeStatus(friendship, FriendshipStatus.Removed, DateTimeOffset.UtcNow);
        AddFriendshipChangedEvent(friendship, me, FriendshipChange.Removed);
        return await SaveOrConflictAsync(ct);
    }

    // MỌI lần đổi trạng thái đi qua đây → Revision luôn +1 (số phiên bản = đồng hồ logic của riêng cặp này).
    // Lưu cùng SaveChangesAsync có kiểm tra xmin: 2 request cùng đọc Revision = 5 thì chỉ một bên ghi được 6,
    // bên kia thua xmin → không bao giờ có 2 lần đổi cùng mang số 6.
    private static void ChangeStatus(Friendship f, FriendshipStatus status, DateTimeOffset now)
    {
        f.Status = status;
        f.Revision++;
        f.UpdatedAt = now;
    }

    // Accepted và Removed chung MỘT topic, key = cặp userId → cùng partition → group-service nhận đúng thứ tự
    // (chấp nhận → hủy → chấp nhận lại của cùng một cặp không bao giờ bị đảo).
    // Revision đi kèm để group-service bỏ được sự kiện cũ được giao lại muộn (thứ tự Kafka không chặn được việc đó).
    // Lời mời / từ chối / hủy lời mời KHÔNG phát sự kiện (vẫn tăng Revision): chưa service nào cần biết.
    private void AddFriendshipChangedEvent(Friendship f, Guid actorId, string change) =>
        db.AddOutboxEvent(KafkaTopics.FriendshipChanged, DirectChat.PairKey(f.UserLowId, f.UserHighId),
            new FriendshipChanged(f.UserLowId, f.UserHighId, actorId, change, f.Revision));

    // Tìm dòng của cặp (me, other) theo PK đã sắp low < high. Có theo dõi thay đổi để sửa rồi lưu.
    private Task<Friendship?> FindAsync(Guid me, Guid otherId, CancellationToken ct)
    {
        var (low, high) = DirectChat.Order(me, otherId);
        return db.Friendships.FirstOrDefaultAsync(f => f.UserLowId == low && f.UserHighId == high, ct);
    }

    private async Task<ServiceResult> SaveOrConflictAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return ServiceResult.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.Fail(ServiceError.Conflict, "Lời mời vừa bị thay đổi, hãy tải lại");
        }
    }

    private async Task<ServiceResult<FriendDto>> FriendResultAsync(Guid me, Guid otherId, Friendship f, CancellationToken ct)
    {
        var other = await db.Users.AsNoTracking().FirstAsync(u => u.Id == otherId, ct);
        return ServiceResult<FriendDto>.Ok(new FriendDto(other.Id, other.UserName, other.DisplayName,
            f.RespondedAt ?? f.UpdatedAt, DirectChat.GroupIdFor(me, otherId)));
    }

    // Lý do không gửi lời mời được khi dòng đang Accepted / Pending.
    private static string DescribeBlocking(Friendship f, Guid me) => f.Status switch
    {
        FriendshipStatus.Accepted => "Hai bạn đã là bạn bè",
        FriendshipStatus.Pending when f.RequesterId == me => "Bạn đã gửi lời mời cho người này",
        FriendshipStatus.Pending => "Người này đã mời bạn, hãy chấp nhận lời mời",
        _ => "Lời mời vừa bị thay đổi, hãy thử lại"
    };
}
