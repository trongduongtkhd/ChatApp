using ChatApp.GroupService.Data;
using ChatApp.GroupService.Dtos;
using ChatApp.GroupService.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatApp.GroupService.Services;

public class GroupManagementService(GroupDbContext db)
{
    // Mã lỗi PostgreSQL khi vi phạm unique/primary key.
    private const string UniqueViolation = "23505";

    public async Task<ServiceResult<GroupDto>> CreateAsync(Guid currentUserId, CreateGroupRequest request, CancellationToken ct)
    {
        var group = new Group
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            OwnerId = currentUserId
        };
        group.Members.Add(new GroupMember { UserId = currentUserId, Role = GroupRole.Owner });

        db.Groups.Add(group);
        // Một SaveChangesAsync = một transaction: INSERT groups + INSERT group_members
        // cùng thành công hoặc cùng bị hủy → không bao giờ có nhóm không có chủ.
        await db.SaveChangesAsync(ct);

        // TODO Phần 5: phát group.member-added cho Owner.

        return ServiceResult<GroupDto>.Ok(ToDto(group, GroupRole.Owner));
    }

    public async Task<IReadOnlyList<GroupDto>> ListMineAsync(Guid currentUserId, CancellationToken ct)
    {
        var rows = await (
                from m in db.GroupMembers
                where m.UserId == currentUserId
                join g in db.Groups on m.GroupId equals g.Id
                orderby g.CreatedAt descending
                select new { Group = g, m.Role })
            .AsNoTracking()
            .ToListAsync(ct);

        return rows.Select(r => ToDto(r.Group, r.Role)).ToList();
    }

    public async Task<ServiceResult<GroupDetailDto>> GetDetailAsync(Guid currentUserId, Guid groupId, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return ServiceResult<GroupDetailDto>.Fail(ServiceError.NotFound, "Không tìm thấy nhóm");

        // LEFT JOIN user_snapshots: thành viên vẫn hiện dù bản sao tên chưa đồng bộ.
        var members = await (
                from m in db.GroupMembers
                where m.GroupId == groupId
                join s in db.UserSnapshots on m.UserId equals s.UserId into snapshots
                from s in snapshots.DefaultIfEmpty()
                orderby m.JoinedAt
                select new { m.UserId, UserName = (string?)s!.UserName, DisplayName = (string?)s!.DisplayName, m.Role, m.JoinedAt })
            .AsNoTracking()
            .ToListAsync(ct);

        var myMembership = members.FirstOrDefault(m => m.UserId == currentUserId);
        if (myMembership is null)
            return ServiceResult<GroupDetailDto>.Fail(ServiceError.Forbidden, "Bạn không phải thành viên nhóm này");

        var memberDtos = members
            .Select(m => new MemberDto(m.UserId, m.UserName, m.DisplayName, m.Role.ToString(), m.JoinedAt))
            .ToList();

        return ServiceResult<GroupDetailDto>.Ok(new GroupDetailDto(ToDto(group, myMembership.Role), memberDtos));
    }

    public async Task<ServiceResult<GroupDto>> UpdateAsync(Guid currentUserId, Guid groupId, UpdateGroupRequest request, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return ServiceResult<GroupDto>.Fail(ServiceError.NotFound, "Không tìm thấy nhóm");
        if (group.OwnerId != currentUserId)
            return ServiceResult<GroupDto>.Fail(ServiceError.Forbidden, "Chỉ Owner được sửa nhóm");

        // Optimistic Locking: báo cho EF rằng "phiên bản tôi đã đọc" là version client gửi lên.
        // EF sinh: UPDATE groups SET ... WHERE id = @id AND xmin = @version
        // Nếu người khác đã sửa trước → xmin đã đổi → 0 dòng bị cập nhật → DbUpdateConcurrencyException.
        db.Entry(group).Property(g => g.Version).OriginalValue = request.Version!.Value;

        group.Name = request.Name.Trim();
        group.Description = request.Description?.Trim();
        group.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<GroupDto>.Fail(ServiceError.Conflict,
                "Nhóm đã bị người khác sửa, hãy tải lại rồi sửa tiếp");
        }

        // Sau SaveChanges, Npgsql đọc lại xmin mới vào group.Version → client dùng cho lần sửa sau.
        return ServiceResult<GroupDto>.Ok(ToDto(group, GroupRole.Owner));
    }

    public async Task<ServiceResult> DeleteAsync(Guid currentUserId, Guid groupId, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return ServiceResult.Fail(ServiceError.NotFound, "Không tìm thấy nhóm");
        if (group.OwnerId != currentUserId)
            return ServiceResult.Fail(ServiceError.Forbidden, "Chỉ Owner được xóa nhóm");

        // ON DELETE CASCADE: PostgreSQL tự xóa các dòng group_members của nhóm.
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);

        // TODO Phần 5: phát group.member-removed cho từng thành viên của nhóm.

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<MemberDto>> AddMemberAsync(Guid currentUserId, Guid groupId, Guid userId, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return ServiceResult<MemberDto>.Fail(ServiceError.NotFound, "Không tìm thấy nhóm");
        if (group.OwnerId != currentUserId)
            return ServiceResult<MemberDto>.Fail(ServiceError.Forbidden, "Chỉ Owner được thêm thành viên");

        // Không có FK sang identity_db → tự kiểm tra user có thật qua bản sao user_snapshots.
        var snapshot = await db.UserSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (snapshot is null)
            return ServiceResult<MemberDto>.Fail(ServiceError.NotFound, "User không tồn tại hoặc chưa được đồng bộ sang group-service");

        if (await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId, ct))
            return ServiceResult<MemberDto>.Fail(ServiceError.Conflict, "User đã là thành viên nhóm");

        var member = new GroupMember { GroupId = groupId, UserId = userId, Role = GroupRole.Member };
        db.GroupMembers.Add(member);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // 2 request thêm cùng user đến cùng lúc: PK (group_id, user_id) chỉ cho một cái thành công.
            return ServiceResult<MemberDto>.Fail(ServiceError.Conflict, "User đã là thành viên nhóm");
        }

        // TODO Phần 5: phát group.member-added.

        return ServiceResult<MemberDto>.Ok(
            new MemberDto(userId, snapshot.UserName, snapshot.DisplayName, member.Role.ToString(), member.JoinedAt));
    }

    public async Task<ServiceResult> RemoveMemberAsync(Guid currentUserId, Guid groupId, Guid userId, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return ServiceResult.Fail(ServiceError.NotFound, "Không tìm thấy nhóm");

        // Owner xóa người khác, hoặc chính người đó tự rời nhóm.
        var isOwner = group.OwnerId == currentUserId;
        var isSelf = userId == currentUserId;
        if (!isOwner && !isSelf)
            return ServiceResult.Fail(ServiceError.Forbidden, "Chỉ Owner hoặc chính thành viên đó được xóa");

        if (userId == group.OwnerId)
            return ServiceResult.Fail(ServiceError.BadRequest, "Owner không thể rời nhóm, hãy xóa nhóm");

        var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct);
        if (member is null)
            return ServiceResult.Fail(ServiceError.NotFound, "User không phải thành viên nhóm");

        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync(ct);

        // TODO Phần 5: phát group.member-removed.

        return ServiceResult.Ok();
    }

    public async Task<IReadOnlyList<UserSnapshotDto>> SearchUsersAsync(string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return [];

        // ILIKE: tìm không phân biệt hoa thường. Escape % và _ để người dùng gõ vào không thành ký tự đại diện.
        var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return await db.UserSnapshots
            .AsNoTracking()
            .Where(s => EF.Functions.ILike(s.UserName, pattern) || EF.Functions.ILike(s.DisplayName, pattern))
            .OrderBy(s => s.UserName)
            .Take(20)
            .Select(s => new UserSnapshotDto(s.UserId, s.UserName, s.DisplayName))
            .ToListAsync(ct);
    }

    private static GroupDto ToDto(Group g, GroupRole myRole) =>
        new(g.Id, g.Name, g.Description, g.OwnerId, myRole.ToString(), g.Version, g.CreatedAt, g.UpdatedAt);
}
