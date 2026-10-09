using ChatApp.Common.Auth;
using ChatApp.IdentityService.Dtos;
using ChatApp.IdentityService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.IdentityService.Controllers;

// Bạn bè (Phần 11). Lời mời được gọi theo userId của NGƯỜI KIA (không có friendshipId):
// cặp (mình, người kia) chính là định danh của quan hệ. "Mình" luôn lấy từ claim sub của JWT.
[ApiController]
[Route("api/friends")]
[Authorize]
public class FriendsController(FriendshipService friendships) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FriendDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await friendships.ListFriendsAsync(User.GetUserId(), ct));

    [HttpGet("requests/incoming")]
    [ProducesResponseType<IReadOnlyList<FriendRequestDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Incoming(CancellationToken ct) =>
        Ok(await friendships.ListIncomingAsync(User.GetUserId(), ct));

    [HttpGet("requests/outgoing")]
    [ProducesResponseType<IReadOnlyList<FriendRequestDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Outgoing(CancellationToken ct) =>
        Ok(await friendships.ListOutgoingAsync(User.GetUserId(), ct));

    [HttpPost("requests")]
    [ProducesResponseType<FriendRequestDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Send(SendFriendRequest request, CancellationToken ct)
    {
        var result = await friendships.SendRequestAsync(User.GetUserId(), request.UserId!.Value, ct);
        return this.ToActionResult(result, () => StatusCode(StatusCodes.Status201Created, result.Value));
    }

    [HttpPost("requests/{userId:guid}/accept")]
    [ProducesResponseType<FriendDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Accept(Guid userId, CancellationToken ct)
    {
        var result = await friendships.AcceptAsync(User.GetUserId(), userId, ct);
        return this.ToActionResult(result, () => Ok(result.Value));
    }

    [HttpPost("requests/{userId:guid}/decline")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decline(Guid userId, CancellationToken ct)
    {
        var result = await friendships.DeclineAsync(User.GetUserId(), userId, ct);
        return this.ToActionResult(result, NoContent);
    }

    // Hủy lời mời mình đã gửi.
    [HttpDelete("requests/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid userId, CancellationToken ct)
    {
        var result = await friendships.CancelAsync(User.GetUserId(), userId, ct);
        return this.ToActionResult(result, NoContent);
    }

    // Hủy kết bạn.
    [HttpDelete("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken ct)
    {
        var result = await friendships.RemoveFriendAsync(User.GetUserId(), userId, ct);
        return this.ToActionResult(result, NoContent);
    }
}
