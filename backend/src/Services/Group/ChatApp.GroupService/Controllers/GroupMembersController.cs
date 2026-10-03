using ChatApp.Common.Auth;
using ChatApp.GroupService.Dtos;
using ChatApp.GroupService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.GroupService.Controllers;

[ApiController]
[Route("api/groups/{groupId:guid}/members")]
[Authorize]
public class GroupMembersController(GroupManagementService groupService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<MemberDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Add(Guid groupId, AddMemberRequest request, CancellationToken ct)
    {
        var result = await groupService.AddMemberAsync(User.GetUserId(), groupId, request.UserId!.Value, ct);
        return this.ToActionResult(result, () => StatusCode(StatusCodes.Status201Created, result.Value));
    }

    [HttpDelete("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Remove(Guid groupId, Guid userId, CancellationToken ct)
    {
        var result = await groupService.RemoveMemberAsync(User.GetUserId(), groupId, userId, ct);
        return this.ToActionResult(result, NoContent);
    }
}
