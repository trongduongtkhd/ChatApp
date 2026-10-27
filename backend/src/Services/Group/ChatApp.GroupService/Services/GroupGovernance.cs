using System.Security.Cryptography;
using System.Text;
using ChatApp.GroupService.Dtos;
using ChatApp.GroupService.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.GroupService.Services;

public partial class GroupManagementService
{
    private Task<Group?> LockGroupAsync(Guid groupId, CancellationToken ct) =>
        db.Groups.FromSqlInterpolated($"SELECT *, xmin FROM groups WHERE id = {groupId} FOR UPDATE").SingleOrDefaultAsync(ct);

    private async Task<GroupRole?> RoleAsync(Guid groupId, Guid actor, CancellationToken ct) =>
        await db.GroupMembers.Where(m => m.GroupId == groupId && m.UserId == actor).Select(m => (GroupRole?)m.Role).SingleOrDefaultAsync(ct);

    private async Task<ServiceResult?> AccessErrorAsync(Group? group, Guid actor, bool manage, CancellationToken ct)
    {
        if (group is null) return ServiceResult.Fail(ServiceError.NotFound, "Không tìm thấy nhóm.");
        if (group.IsDirect) return ServiceResult.Fail(ServiceError.BadRequest, DirectChatNotAllowed);
        var role = await RoleAsync(group.Id, actor, ct);
        if (role is null || (manage && role is not (GroupRole.Owner or GroupRole.Admin)))
            return ServiceResult.Fail(ServiceError.Forbidden, "Bạn không có quyền thực hiện thao tác này.");
        return null;
    }
    private void Audit(Group group, Guid actor, Guid? target, string action)
    {
        group.UpdatedAt = DateTimeOffset.UtcNow;
        db.GroupAudits.Add(new GroupAudit { GroupId = group.Id, ActorId = actor, TargetId = target, Action = action });
    }
    private async Task<bool> ActiveAsync(Guid id, CancellationToken ct) =>
        (await accounts.ReadAsync(new[] { id }, ct)).GetValueOrDefault(id) is { IsLocked: false };

    private async Task<JoinRequestDto> RequestDtoAsync(GroupJoinRequest r, CancellationToken ct)
    {
        var user = await db.UserSnapshots.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == r.UserId, ct);
        return new(r.Id, r.GroupId, r.UserId, user?.UserName, user?.DisplayName, r.RequestedBy, r.Status, r.CreatedAt, r.ReviewedBy, r.ReviewedAt);
    }

    private async Task<ServiceResult<MembershipResult>> JoinCoreAsync(Group group, Guid target, Guid actor, bool manager, CancellationToken ct)
    {
        if (!await ActiveAsync(target, ct)) return ServiceResult<MembershipResult>.Fail(ServiceError.BadRequest, "Tài khoản không tồn tại hoặc đang bị khóa.");
        var old = await db.GroupMembers.AsNoTracking().SingleOrDefaultAsync(m => m.GroupId == group.Id && m.UserId == target, ct);
        if (old is not null) return ServiceResult<MembershipResult>.Fail(ServiceError.Conflict, "User đã là thành viên nhóm.");
        var pending = await db.JoinRequests.SingleOrDefaultAsync(r => r.GroupId == group.Id && r.UserId == target && r.Status == "Pending", ct);
        if (group.RequiresApproval && !manager)
        {
            if (pending is null)
            {
                pending = new GroupJoinRequest { GroupId = group.Id, UserId = target, RequestedBy = actor };
                db.JoinRequests.Add(pending);
                Audit(group, actor, target, "JoinRequested");
            }
            return ServiceResult<MembershipResult>.Ok(new(null, await RequestDtoAsync(pending, ct)));
        }
        var member = new GroupMember { GroupId = group.Id, UserId = target, Role = GroupRole.Member };
        db.GroupMembers.Add(member);
        AddMemberAddedEvent(group.Id, target, GroupRole.Member);
        if (pending is not null) { pending.Status = "Approved"; pending.ReviewedBy = actor; pending.ReviewedAt = DateTimeOffset.UtcNow; }
        Audit(group, actor, target, "MemberAdded");
        var user = await db.UserSnapshots.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == target, ct);
        return ServiceResult<MembershipResult>.Ok(new(new(target, user?.UserName, user?.DisplayName, "Member", member.JoinedAt), null));
    }

    public async Task<ServiceResult> SetRoleAsync(Guid actor, Guid groupId, Guid target, GroupRoleRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var group = await LockGroupAsync(groupId, ct);
        var error = await AccessErrorAsync(group, actor, true, ct);
        if (error is not null) return error;
        if (group!.OwnerId != actor) return ServiceResult.Fail(ServiceError.Forbidden, "Chỉ trưởng nhóm được bổ nhiệm quản trị viên.");
        if (group.Version != request.Version) return ServiceResult.Fail(ServiceError.Conflict, "Nhóm đã thay đổi. Hãy tải lại.");
        var member = await db.GroupMembers.SingleOrDefaultAsync(m => m.GroupId == groupId && m.UserId == target, ct);
        if (member is null) return ServiceResult.Fail(ServiceError.NotFound, "Người này chưa là thành viên.");
        if (member.Role == GroupRole.Owner) return ServiceResult.Fail(ServiceError.BadRequest, "Dùng chức năng chuyển trưởng nhóm để đổi Owner.");
        if (request.Role == "Admin" && !await ActiveAsync(target, ct)) return ServiceResult.Fail(ServiceError.BadRequest, "Tài khoản đang bị khóa.");
        if (request.Role is not ("Admin" or "Member")) return ServiceResult.Fail(ServiceError.BadRequest, "Vai trò không hợp lệ.");
        member.Role = Enum.Parse<GroupRole>(request.Role);
        Audit(group, actor, target, "Role:" + request.Role);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> TransferOwnerAsync(Guid actor, Guid groupId, TransferOwnerRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var group = await LockGroupAsync(groupId, ct);
        var error = await AccessErrorAsync(group, actor, true, ct);
        if (error is not null) return error;
        if (group!.OwnerId != actor) return ServiceResult.Fail(ServiceError.Forbidden, "Chỉ trưởng nhóm được chuyển quyền.");
        if (group.Version != request.Version) return ServiceResult.Fail(ServiceError.Conflict, "Nhóm đã thay đổi. Hãy tải lại.");
        if (request.UserId == actor) return ServiceResult.Fail(ServiceError.BadRequest, "Bạn đang là trưởng nhóm.");
        var target = await db.GroupMembers.SingleOrDefaultAsync(m => m.GroupId == groupId && m.UserId == request.UserId, ct);
        if (target is null || !await ActiveAsync(request.UserId, ct)) return ServiceResult.Fail(ServiceError.BadRequest, "Cần chọn một thành viên đang hoạt động.");
        // Hạ Owner cũ trước để không chạm unique index tức thời; cả hai câu vẫn cùng transaction.
        await db.GroupMembers.Where(m => m.GroupId == groupId && m.UserId == actor)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, GroupRole.Member), ct);
        target.Role = GroupRole.Owner;
        group.OwnerId = target.UserId;
        Audit(group, actor, target.UserId, "OwnerTransferred");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SetApprovalAsync(Guid actor, Guid groupId, ApprovalSettingRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var group = await LockGroupAsync(groupId, ct);
        var error = await AccessErrorAsync(group, actor, true, ct);
        if (error is not null) return error;
        if (group!.OwnerId != actor) return ServiceResult.Fail(ServiceError.Forbidden, "Chỉ trưởng nhóm được đổi cài đặt.");
        if (group.Version != request.Version) return ServiceResult.Fail(ServiceError.Conflict, "Nhóm đã thay đổi. Hãy tải lại.");
        group.RequiresApproval = request.RequiresApproval;
        Audit(group, actor, null, request.RequiresApproval ? "ApprovalEnabled" : "ApprovalDisabled");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<IReadOnlyList<JoinRequestDto>>> RequestsAsync(Guid actor, Guid groupId, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId, ct);
        var error = await AccessErrorAsync(group, actor, true, ct);
        if (error is not null) return ServiceResult<IReadOnlyList<JoinRequestDto>>.Fail(error.Error, error.Message!);
        var rows = await (from r in db.JoinRequests.AsNoTracking()
            where r.GroupId == groupId && r.Status == "Pending"
            join u in db.UserSnapshots on r.UserId equals u.UserId into users
            from u in users.DefaultIfEmpty()
            orderby r.CreatedAt, r.Id
            select new JoinRequestDto(r.Id, r.GroupId, r.UserId, u == null ? null : u.UserName,
                u == null ? null : u.DisplayName, r.RequestedBy, r.Status, r.CreatedAt, r.ReviewedBy, r.ReviewedAt)).Take(200).ToArrayAsync(ct);
        return ServiceResult<IReadOnlyList<JoinRequestDto>>.Ok(rows);
    }

    public async Task<ServiceResult> DecideAsync(Guid actor, Guid groupId, Guid requestId, bool approve, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var group = await LockGroupAsync(groupId, ct);
        var error = await AccessErrorAsync(group, actor, true, ct);
        if (error is not null) return error;
        var request = await db.JoinRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.GroupId == groupId, ct);
        if (request is null) return ServiceResult.Fail(ServiceError.NotFound, "Không tìm thấy yêu cầu.");
        var status = approve ? "Approved" : "Rejected";
        if (request.Status != "Pending") return request.Status == status ? ServiceResult.Ok() : ServiceResult.Fail(ServiceError.Conflict, "Yêu cầu đã được xử lý.");
        if (approve)
        {
            if (!await ActiveAsync(request.UserId, ct)) return ServiceResult.Fail(ServiceError.BadRequest, "Tài khoản đang bị khóa.");
            if (!await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == request.UserId, ct))
            {
                db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = request.UserId, Role = GroupRole.Member });
                AddMemberAddedEvent(groupId, request.UserId, GroupRole.Member);
            }
        }
        request.Status = status; request.ReviewedBy = actor; request.ReviewedAt = DateTimeOffset.UtcNow;
        Audit(group!, actor, request.UserId, "Join:" + status);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<string>> RotateInviteAsync(Guid actor, Guid groupId, uint version, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var group = await LockGroupAsync(groupId, ct);
        var error = await AccessErrorAsync(group, actor, true, ct);
        if (error is not null) return ServiceResult<string>.Fail(error.Error, error.Message!);
        if (group!.Version != version) return ServiceResult<string>.Fail(ServiceError.Conflict, "Nhóm đã thay đổi. Hãy tải lại.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        group.InviteTokenHash = HashToken(token);
        Audit(group, actor, null, "InviteRotated");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ServiceResult<string>.Ok(token);
    }
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<object?> PreviewInviteAsync(Guid actor, string token, CancellationToken ct)
    {
        if (token.Length != 48 || token.Any(c => !Uri.IsHexDigit(c))) return null;
        var hash = HashToken(token.ToLowerInvariant());
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => !g.IsDirect && g.InviteTokenHash == hash, ct);
        if (group is null) return null;
        var status = await db.JoinRequests.AsNoTracking().Where(r => r.GroupId == group.Id && r.UserId == actor)
            .OrderByDescending(r => r.CreatedAt).Select(r => r.Status).FirstOrDefaultAsync(ct);
        return new { groupId = group.Id, name = group.Name, description = group.Description, group.RequiresApproval,
            isMember = await db.GroupMembers.AnyAsync(m => m.GroupId == group.Id && m.UserId == actor, ct), requestStatus = status };
    }
    public async Task<ServiceResult<MembershipResult>> JoinInviteAsync(Guid actor, string token, CancellationToken ct)
    {
        if (token.Length != 48 || token.Any(c => !Uri.IsHexDigit(c))) return ServiceResult<MembershipResult>.Fail(ServiceError.NotFound, "Liên kết không hợp lệ hoặc đã đổi.");
        var hash = HashToken(token.ToLowerInvariant());
        var id = await db.Groups.AsNoTracking().Where(g => !g.IsDirect && g.InviteTokenHash == hash).Select(g => (Guid?)g.Id).SingleOrDefaultAsync(ct);
        if (id is null) return ServiceResult<MembershipResult>.Fail(ServiceError.NotFound, "Liên kết không hợp lệ hoặc đã đổi.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var group = await LockGroupAsync(id.Value, ct);
        if (group is null || group.InviteTokenHash != hash) return ServiceResult<MembershipResult>.Fail(ServiceError.NotFound, "Liên kết đã đổi.");
        var result = await JoinCoreAsync(group, actor, actor, false, ct);
        if (result.IsSuccess) { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        return result;
    }
    public async Task<ServiceResult> CancelRequestAsync(Guid actor, Guid groupId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var group = await LockGroupAsync(groupId, ct);
        if (group is null || group.IsDirect) return ServiceResult.Fail(ServiceError.NotFound, "Không tìm thấy yêu cầu.");
        var request = await db.JoinRequests.SingleOrDefaultAsync(r => r.GroupId == groupId && r.UserId == actor && r.Status == "Pending", ct);
        if (request is null) return ServiceResult.Fail(ServiceError.NotFound, "Không có yêu cầu đang chờ của bạn.");
        request.Status = "Cancelled"; request.ReviewedAt = DateTimeOffset.UtcNow; request.ReviewedBy = actor;
        Audit(group, actor, actor, "Join:Cancelled");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ServiceResult.Ok();
    }
}
