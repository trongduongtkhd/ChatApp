using ChatApp.Common.Auth;
using ChatApp.IdentityService.Dtos;
using ChatApp.IdentityService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.IdentityService.Controllers;

[ApiController]
[Route("api/users")]
[Authorize] // Không có token hợp lệ → 401, chưa vào tới action.
public class UsersController(AuthService authService) : ControllerBase
{
    [HttpGet("me")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        // UserId lấy từ claim "sub" trong token đã được xác minh chữ ký, client không tự khai được.
        var user = await authService.GetByIdAsync(User.GetUserId(), ct);
        return user is null ? NotFound() : Ok(user);
    }
}
