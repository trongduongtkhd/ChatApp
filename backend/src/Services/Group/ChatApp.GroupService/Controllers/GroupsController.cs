using ChatApp.Common.Auth;
using ChatApp.GroupService.Dtos;
using ChatApp.GroupService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.GroupService.Controllers;

[ApiController]
[Route("api/groups")]
[Authorize]
public class GroupsController(GroupManagementService groupService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<GroupDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateGroupRequest request, CancellationToken ct)
    {
        var result = await groupService.CreateAsync(User.GetUserId(), request, ct);
        return this.ToActionResult(result,
            () => CreatedAtAction(nameof(GetDetail), new { groupId = result.Value!.Id }, result.Value));
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<GroupDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMine(CancellationToken ct) =>
        Ok(await groupService.ListMineAsync(User.GetUserId(), ct));

    // {groupId:guid}: chỉ khớp khi đoạn đường dẫn là GUID → "/api/groups/users/..." không bị hiểu nhầm là groupId.
    [HttpGet("{groupId:guid}")]
    [ProducesResponseType<GroupDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid groupId, CancellationToken ct)
    {
        var result = await groupService.GetDetailAsync(User.GetUserId(), groupId, ct);
        return this.ToActionResult(result, () => Ok(result.Value));
    }

    [HttpPut("{groupId:guid}")]
    [ProducesResponseType<GroupDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid groupId, UpdateGroupRequest request, CancellationToken ct)
    {
        var result = await groupService.UpdateAsync(User.GetUserId(), groupId, request, ct);
        return this.ToActionResult(result, () => Ok(result.Value));
    }

    [HttpDelete("{groupId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(Guid groupId, CancellationToken ct)
    {
        var result = await groupService.DeleteAsync(User.GetUserId(), groupId, ct);
        return this.ToActionResult(result, NoContent);
    }
}
