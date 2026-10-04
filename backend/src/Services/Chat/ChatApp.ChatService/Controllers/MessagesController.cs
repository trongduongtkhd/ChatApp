using ChatApp.ChatService.Dtos;
using ChatApp.ChatService.Services;
using ChatApp.Common.Auth;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.ChatService.Controllers;

// REST lịch sử tin nhắn (DESIGN mục 5). Tin MỚI đi qua SignalR (ReceiveMessage), tin CŨ tải qua REST:
// mở nhóm → GET trang đầu; cuộn lên → GET với beforeSeq.
[ApiController]
[Route("api/chat/groups/{groupId:guid}/messages")]
[Authorize]
public class MessagesController(
    MessageService messages,
    GroupMemberCache members,
    ILogger<MessagesController> logger) : ControllerBase
{
    private const int MaxLimit = 100;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MessageDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetHistory(
        Guid groupId, [FromQuery] long? beforeSeq, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        // Giới hạn trên: không cho một request kéo cả triệu tin (tốn DB, tốn mạng).
        if (limit is < 1 or > MaxLimit)
            return Problem(title: $"limit phải từ 1 đến {MaxLimit}.", statusCode: StatusCodes.Status400BadRequest);

        // Cùng cách kiểm tra với hub: cache Redis, trống thì gRPC. Người ngoài nhóm không đọc được lịch sử.
        bool isMember;
        try
        {
            isMember = await members.IsMemberAsync(groupId, User.GetUserId(), ct);
        }
        catch (RpcException ex)
        {
            logger.LogWarning("Không kiểm tra được thành viên nhóm {GroupId}: gRPC {Status}", groupId, ex.StatusCode);
            return Problem(title: "Không kiểm tra được thành viên nhóm, thử lại sau.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (!isMember)
            return Problem(title: "Bạn không phải thành viên nhóm này.", statusCode: StatusCodes.Status403Forbidden);

        return Ok(await messages.GetHistoryAsync(groupId, beforeSeq, limit, ct));
    }
}
