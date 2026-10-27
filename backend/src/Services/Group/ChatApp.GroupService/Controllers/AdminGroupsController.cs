using ChatApp.Common.Auth;
using ChatApp.GroupService.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.GroupService.Controllers;

[ApiController, Route("api/admin/groups"), Authorize(Policy = AccountAccessRules.SystemAdmin)]
public sealed class AdminGroupsController(GroupDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string? q, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (page < 1 || page > 100000 || pageSize < 1 || pageSize > 100 || q?.Length > 100) return BadRequest();
        var query = db.Groups.AsNoTracking().Where(g => !g.IsDirect);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(g => EF.Functions.ILike(g.Name, pattern));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(g => g.CreatedAt).ThenBy(g => g.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(g => new { g.Id, g.Name, g.Description, g.OwnerId, g.RequiresApproval, g.CreatedAt,
                memberCount = g.Members.Count,
                ownerName = db.UserSnapshots.Where(u => u.UserId == g.OwnerId).Select(u => u.DisplayName).FirstOrDefault() }).ToArrayAsync(ct);
        return Ok(new { items = rows, total, page, pageSize });
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (page < 1 || page > 100000 || pageSize < 1 || pageSize > 100) return BadRequest();
        var group = await db.Groups.AsNoTracking().Where(g => g.Id == id && !g.IsDirect)
            .Select(g => new { g.Id, g.Name, g.Description, g.OwnerId, g.RequiresApproval, g.CreatedAt }).SingleOrDefaultAsync(ct);
        if (group is null) return NotFound();
        var query = db.GroupMembers.AsNoTracking().Where(m => m.GroupId == id);
        var total = await query.CountAsync(ct);
        var members = await (from m in query
            join u in db.UserSnapshots on m.UserId equals u.UserId into snapshots
            from u in snapshots.DefaultIfEmpty()
            orderby m.Role == Entities.GroupRole.Owner ? 0 : m.Role == Entities.GroupRole.Admin ? 1 : 2, m.JoinedAt, m.UserId
            select new { m.UserId, userName = u == null ? null : u.UserName, displayName = u == null ? null : u.DisplayName,
                role = m.Role.ToString(), m.JoinedAt }).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        var audit = await db.GroupAudits.AsNoTracking().Where(a => a.GroupId == id).OrderByDescending(a => a.CreatedAt).Take(30).ToArrayAsync(ct);
        return Ok(new { group, members = new { items = members, total, page, pageSize }, audit });
    }
}
