using ChatApp.Common.Auth;
using ChatApp.NotificationService.Dtos;
using ChatApp.NotificationService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.NotificationService.Controllers;

// REST số tin chưa đọc (DESIGN mục 5). Angular: đăng nhập → GET /unread lấy số ban đầu;
// sau đó nghe UnreadCountChanged qua /hubs/notifications (Bước 5); mở nhóm → POST .../read.
// UserId luôn lấy từ JWT (claim sub), KHÔNG nhận từ client → không ai đọc/sửa được bộ đếm của người khác.
[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController(UnreadCounterService counters, UnreadNotifier notifier) : ControllerBase
{
    [HttpGet("unread")]
    [ProducesResponseType<IReadOnlyList<UnreadCounterDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUnread(CancellationToken ct) =>
        Ok(await counters.GetForUserAsync(User.GetUserId(), ct));

    [HttpPost("groups/{groupId:guid}/read")]
    [ProducesResponseType<UnreadCounterDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> MarkRead(Guid groupId, MarkReadRequest request, CancellationToken ct)
    {
        if (request.LastReadSequence is not { } seq || seq < 0)
            return Problem(title: "lastReadSequence là bắt buộc và phải >= 0.", statusCode: StatusCodes.Status400BadRequest);

        // Quyền dựa trên BẢN SAO thành viên (không gọi group-service) → group-service chết vẫn đánh dấu được.
        // Đổi lại: người vừa được thêm có thể bị 403 khoảng ~1 giây, tới khi member-added tới (eventual consistency).
        var userId = User.GetUserId();
        var result = await counters.MarkReadAsync(userId, groupId, seq, ct);
        if (result is null)
            return Problem(title: "Bạn không phải thành viên nhóm này.", statusCode: StatusCodes.Status403Forbidden);

        // Đẩy số mới tới MỌI kết nối của chính user này: đọc ở tab A thì chấm đỏ ở tab B cũng tắt.
        await notifier.NotifyAsync(groupId, [new UnreadChange(userId, result.UnreadCount)]);
        return Ok(result);
    }
}
