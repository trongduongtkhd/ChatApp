using System.ComponentModel.DataAnnotations;
using ChatApp.Common.Auth;
using ChatApp.Common.Outbox;
using ChatApp.Contracts.Events;
using ChatApp.IdentityService.Data;
using ChatApp.IdentityService.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.IdentityService.Controllers;

public record AccountLockRequest(bool IsLocked, uint Version, [MaxLength(500)] string? Reason);
public record AdminUserDto(Guid Id, string UserName, string DisplayName, string Email, DateTimeOffset CreatedAt,
    string SystemRole, bool IsLocked, DateTimeOffset? LockedAt, Guid? LockedBy, string? LockReason, uint Version);

[ApiController, Route("api/admin/users"), Authorize(Policy = AccountAccessRules.SystemAdmin)]
public sealed class AdminUsersController(IdentityDbContext db) : ControllerBase
{
    private static AdminUserDto ToDto(User u) => new(u.Id, u.UserName, u.DisplayName, u.Email, u.CreatedAt,
        u.SystemRole, u.IsLocked, u.LockedAt, u.LockedBy, u.LockReason, u.Version);

    [HttpGet]
    public async Task<IActionResult> List(string? q, bool? locked, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (page < 1 || page > 100000 || pageSize < 1 || pageSize > 100 || q?.Length > 100) return BadRequest();
        var query = db.Users.AsNoTracking();
        if (locked.HasValue) query = query.Where(u => u.IsLocked == locked.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(u => EF.Functions.ILike(u.UserName, pattern) || EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.Email, pattern));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return Ok(new { items = rows.Select(ToDto), total, page, pageSize });
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        var audit = await db.AccountAudits.AsNoTracking().Where(a => a.TargetId == id)
            .OrderByDescending(a => a.CreatedAt).Take(30).ToArrayAsync(ct);
        return Ok(new { user = ToDto(user), audit });
    }
    [HttpPut("{id:guid}/lock")]
    public async Task<IActionResult> Lock(Guid id, AccountLockRequest request, CancellationToken ct)
    {
        if (request.IsLocked && id == User.GetUserId()) return Problem(statusCode: 400, title: "Không thể tự khóa tài khoản.");
        if (request.IsLocked && string.IsNullOrWhiteSpace(request.Reason)) return Problem(statusCode: 400, title: "Cần nhập lý do khóa.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize quản trị account để hai admin không đồng thời khóa hết admin đang hoạt động.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(908010)", ct);
        var user = await db.Users.FromSqlInterpolated($"SELECT *, xmin FROM users WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (user is null) return NotFound();
        if (user.Version != request.Version) return Problem(statusCode: 409, title: "Tài khoản đã thay đổi. Hãy tải lại.");
        if (user.IsLocked == request.IsLocked) return Ok(ToDto(user));
        if (request.IsLocked && user.SystemRole == AccountAccessRules.SystemAdmin &&
            !await db.Users.AnyAsync(u => u.Id != id && u.SystemRole == AccountAccessRules.SystemAdmin && !u.IsLocked, ct))
            return Problem(statusCode: 400, title: "Phải giữ ít nhất một admin hoạt động.");
        user.IsLocked = request.IsLocked;
        user.AuthVersion++;
        user.LockedAt = request.IsLocked ? DateTimeOffset.UtcNow : null;
        user.LockedBy = request.IsLocked ? User.GetUserId() : null;
        user.LockReason = request.IsLocked ? request.Reason!.Trim() : null;
        db.AccountAudits.Add(new AccountAudit { ActorId = User.GetUserId(), TargetId = id,
            Action = request.IsLocked ? "Locked" : "Unlocked", Reason = request.Reason?.Trim() });
        db.AddOutboxEvent(KafkaTopics.AccountStateChanged, id.ToString(), new AccountStateChanged(id, user.IsLocked, user.AuthVersion));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(ToDto(user));
    }
}
