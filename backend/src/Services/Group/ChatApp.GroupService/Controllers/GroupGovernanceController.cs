using ChatApp.Common.Auth;
using ChatApp.GroupService.Dtos;
using ChatApp.GroupService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.GroupService.Controllers;

[ApiController, Route("api/groups/{groupId:guid}"), Authorize]
public sealed class GroupGovernanceController(GroupManagementService service) : ControllerBase
{
    [HttpPut("members/{userId:guid}/role")]
    public async Task<IActionResult> Role(Guid groupId, Guid userId, GroupRoleRequest request, CancellationToken ct) =>
        this.ToActionResult(await service.SetRoleAsync(User.GetUserId(), groupId, userId, request, ct), NoContent);
    [HttpPut("owner")]
    public async Task<IActionResult> Owner(Guid groupId, TransferOwnerRequest request, CancellationToken ct) =>
        this.ToActionResult(await service.TransferOwnerAsync(User.GetUserId(), groupId, request, ct), NoContent);
    [HttpPut("approval")]
    public async Task<IActionResult> Approval(Guid groupId, ApprovalSettingRequest request, CancellationToken ct) =>
        this.ToActionResult(await service.SetApprovalAsync(User.GetUserId(), groupId, request, ct), NoContent);
    [HttpGet("join-requests")]
    public async Task<IActionResult> Requests(Guid groupId, CancellationToken ct)
    {
        var result = await service.RequestsAsync(User.GetUserId(), groupId, ct);
        return this.ToActionResult(result, () => Ok(result.Value));
    }
    [HttpPut("join-requests/{requestId:guid}")]
    public async Task<IActionResult> Decide(Guid groupId, Guid requestId, JoinDecisionRequest request, CancellationToken ct) =>
        this.ToActionResult(await service.DecideAsync(User.GetUserId(), groupId, requestId, request.Approve, ct), NoContent);
    [HttpDelete("join-requests/mine")]
    public async Task<IActionResult> Cancel(Guid groupId, CancellationToken ct) =>
        this.ToActionResult(await service.CancelRequestAsync(User.GetUserId(), groupId, ct), NoContent);
    [HttpPost("invite")]
    public async Task<IActionResult> Invite(Guid groupId, GroupVersionRequest request, CancellationToken ct)
    {
        var result = await service.RotateInviteAsync(User.GetUserId(), groupId, request.Version, ct);
        return this.ToActionResult(result, () => Ok(new { token = result.Value }));
    }
}

[ApiController, Route("api/groups/invites"), Authorize]
public sealed class GroupInvitesController(GroupManagementService service) : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Preview(string token, CancellationToken ct)
    {
        var result = await service.PreviewInviteAsync(User.GetUserId(), token, ct);
        return result is null ? NotFound(new { title = "Liên kết không hợp lệ hoặc đã đổi." }) : Ok(result);
    }
    [HttpPost("{token}/join")]
    public async Task<IActionResult> Join(string token, CancellationToken ct)
    {
        var result = await service.JoinInviteAsync(User.GetUserId(), token, ct);
        return this.ToActionResult(result, () => result.Value!.Member is { } member
            ? StatusCode(201, member) : StatusCode(202, result.Value.Request));
    }
}
