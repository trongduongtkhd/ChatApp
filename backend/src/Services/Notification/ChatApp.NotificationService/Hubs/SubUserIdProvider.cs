using ChatApp.Common.Auth;
using Microsoft.AspNetCore.SignalR;

namespace ChatApp.NotificationService.Hubs;

// SignalR gom các kết nối theo "UserIdentifier" để Clients.User(id) gửi được tới mọi kết nối của một user.
// Mặc định nó đọc claim ClaimTypes.NameIdentifier (tên URL dài). Nhưng Common đặt MapInboundClaims = false
// (giữ nguyên tên claim "sub") → claim mặc định không tồn tại → UserIdentifier = null → Clients.User không tới ai.
// Lớp này chỉ cho SignalR: định danh user = claim "sub" (= UserId của identity-service, GUID v7).
public sealed class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User.FindFirst(ChatAppClaims.UserId)?.Value;
}
