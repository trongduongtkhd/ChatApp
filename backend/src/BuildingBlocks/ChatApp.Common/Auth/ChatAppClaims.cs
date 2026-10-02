using System.Security.Claims;

namespace ChatApp.Common.Auth;

// Tên claim trong JWT (DESIGN.md mục 5). Mọi service dùng chung hằng số này để đọc đúng tên.
public static class ChatAppClaims
{
    public const string UserId = "sub";
    public const string UserName = "name";
    public const string DisplayName = "display_name";

    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(UserId)
            ?? throw new InvalidOperationException("Token không có claim 'sub'"));

    public static string GetUserName(this ClaimsPrincipal user) =>
        user.FindFirstValue(UserName) ?? "";

    public static string GetDisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(DisplayName) ?? "";
}
