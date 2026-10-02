using ChatApp.IdentityService.Dtos;
using ChatApp.IdentityService.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.IdentityService.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(AuthService authService) : ControllerBase
{
    // [ApiController] tự kiểm tra DataAnnotations trong RegisterRequest, sai thì trả 400.
    [HttpPost("register")]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var result = await authService.RegisterAsync(request, ct);
        if (result.Error is not null)
            return Conflict(new ProblemDetails { Title = result.Error, Status = StatusCodes.Status409Conflict });

        return StatusCode(StatusCodes.Status201Created, result.User);
    }

    [HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var response = await authService.LoginAsync(request, ct);
        if (response is null)
            return Unauthorized(new ProblemDetails
            {
                Title = "Sai tên đăng nhập hoặc mật khẩu",
                Status = StatusCodes.Status401Unauthorized
            });

        return Ok(response);
    }
}
