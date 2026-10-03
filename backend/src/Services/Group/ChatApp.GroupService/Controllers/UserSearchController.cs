using ChatApp.GroupService.Dtos;
using ChatApp.GroupService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.GroupService.Controllers;

// Tìm user để thêm vào nhóm. Tìm trong bản sao user_snapshots của chính group-service,
// KHÔNG gọi sang identity-service.
[ApiController]
[Route("api/groups/users")]
[Authorize]
public class UserSearchController(GroupManagementService groupService) : ControllerBase
{
    [HttpGet("search")]
    [ProducesResponseType<IReadOnlyList<UserSnapshotDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct) =>
        Ok(await groupService.SearchUsersAsync(q, ct));
}
