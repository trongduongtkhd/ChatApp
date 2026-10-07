using ChatApp.ChatService.Dtos;
using ChatApp.ChatService.Services;
using ChatApp.Common.Auth;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatApp.ChatService.Hubs;

// SignalR hub tại /hubs/chat (DESIGN mục 5). Client giữ MỘT kết nối WebSocket lâu dài,
// gọi method của hub (JoinGroup, SendMessage...) và nhận ReceiveMessage do server đẩy xuống bất cứ lúc nào.
//
// [Authorize]: phải có JWT hợp lệ ngay từ lúc kết nối (negotiate). Trình duyệt không gắn được header khi mở WebSocket
// nên token đi qua ?access_token= (đã cấu hình trong ChatApp.Common/Auth, chỉ nhận cho đường dẫn /hubs).
//
// Mỗi lần client gọi một method, SignalR tạo một đối tượng ChatHub MỚI (giống controller) → không lưu trạng thái
// trong field của hub. Trạng thái theo kết nối nằm ở Context (ConnectionId, User).
[Authorize]
public class ChatHub(
    GroupMemberCache members,
    MessageService messages,
    PresenceTracker presence,
    ILogger<ChatHub> logger) : Hub<IChatClient>
{
    private const int MaxContentLength = 4000;
    // Khóa trong Context.Items: các phòng mà KẾT NỐI NÀY đã vào (để báo offline khi ngắt kết nối).
    private const string JoinedRoomsKey = "joined-rooms";

    // "Phòng" SignalR của một nhóm chat. Tên phòng = groupId.
    // Phòng chỉ là danh sách ConnectionId do SignalR giữ trong bộ nhớ của bản chat-service này.
    // Redis Backplane (Program.cs) làm cho lệnh gửi tới phòng có hiệu lực trên MỌI bản: mỗi bản đẩy cho kết nối của mình.
    public static string RoomName(Guid groupId) => groupId.ToString();

    // Context.Items: "túi đồ" riêng của MỘT kết nối, sống từ lúc kết nối tới lúc ngắt, nằm trong RAM của bản đang giữ
    // kết nối đó. Khác field của hub (mỗi lần gọi method là một đối tượng ChatHub mới, field mất hết).
    private HashSet<Guid> JoinedRooms => (HashSet<Guid>)Context.Items[JoinedRoomsKey]!;

    // SignalR gọi khi một kết nối mới mở xong (đã qua [Authorize]).
    public override async Task OnConnectedAsync()
    {
        Context.Items[JoinedRoomsKey] = new HashSet<Guid>();
        var userId = Context.User!.GetUserId();
        var connections = await presence.ConnectedAsync(userId, Context.ConnectionId);
        logger.LogInformation("User {UserId} kết nối {ConnectionId} (đang có {Count} kết nối)",
            userId, Context.ConnectionId, connections);
        await base.OnConnectedAsync();
    }

    // SignalR gọi khi kết nối đóng: client tự đóng, mất mạng (hết thời gian chờ ping), hoặc server tắt đúng cách.
    // KHÔNG được gọi nếu tiến trình chat-service bị giết đột ngột → presence:{userId} còn ConnectionId "ma" (xem báo cáo).
    // SignalR tự gỡ kết nối khỏi mọi phòng, ta chỉ lo phần presence.
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User!.GetUserId();
        var nowOffline = await presence.DisconnectedAsync(userId, Context.ConnectionId);
        logger.LogInformation("User {UserId} ngắt kết nối {ConnectionId}{Offline}",
            userId, Context.ConnectionId, nowOffline ? " → OFFLINE" : " (vẫn còn kết nối khác)");

        // Chỉ báo offline khi đây là kết nối cuối cùng. Đóng 1 trong 2 tab → không báo gì.
        if (nowOffline)
        {
            foreach (var groupId in JoinedRooms)
                await Clients.Group(RoomName(groupId)).UserPresenceChanged(userId, false);
        }
        await base.OnDisconnectedAsync(exception);
    }

    // Vào phòng của nhóm để bắt đầu nhận ReceiveMessage. Chỉ thành viên mới được vào (nếu không, người ngoài nghe lén được).
    // Trả về danh sách userId thành viên ĐANG online (kể cả mình) → client vẽ chấm xanh ngay khi mở nhóm;
    // về sau chỉ cần nghe UserPresenceChanged để cập nhật.
    public async Task<IReadOnlyList<Guid>> JoinGroup(Guid groupId)
    {
        await EnsureMemberAsync(groupId);
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomName(groupId));
        JoinedRooms.Add(groupId);

        var userId = Context.User!.GetUserId();
        logger.LogInformation("User {UserId} (connection {ConnectionId}) vào phòng {GroupId}",
            userId, Context.ConnectionId, groupId);

        // Báo những người đang trong phòng (trừ chính kết nối này) là mình online.
        // Nếu mình đã online từ trước (tab khác) thì họ nhận báo lặp → vô hại, client chỉ đặt lại cờ online = true.
        await Clients.OthersInGroup(RoomName(groupId)).UserPresenceChanged(userId, true);

        var memberIds = await members.GetMemberIdsAsync(groupId, Context.ConnectionAborted);
        return await presence.GetOnlineAsync(memberIds);
    }

    // Rời phòng: không nhận tin của nhóm nữa (vd chuyển sang mở nhóm khác). Không cần kiểm tra thành viên.
    public async Task LeaveGroup(Guid groupId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomName(groupId));
        JoinedRooms.Remove(groupId);
    }

    // Gửi tin (DESIGN mục 6). messageId do client sinh. Trả về tin đã lưu (có sequenceNumber) cho người gửi
    // → client biết chắc server đã nhận; mọi người trong phòng (kể cả người gửi) cũng nhận ReceiveMessage.
    // Gọi lại với cùng messageId bao nhiêu lần cũng chỉ có MỘT tin (idempotent) → client cứ yên tâm gửi lại khi không chắc.
    public async Task<MessageDto> SendMessage(Guid messageId, Guid groupId, string content)
    {
        if (messageId == Guid.Empty)
            throw new HubException("messageId không hợp lệ.");
        content = content?.Trim() ?? "";
        if (content.Length == 0)
            throw new HubException("Nội dung tin nhắn trống.");
        if (content.Length > MaxContentLength)
            throw new HubException($"Tin nhắn dài quá {MaxContentLength} ký tự.");

        await EnsureMemberAsync(groupId);

        var user = Context.User!;
        var displayName = user.GetDisplayName();
        var result = await messages.SendAsync(
            messageId, groupId, user.GetUserId(),
            string.IsNullOrEmpty(displayName) ? user.GetUserName() : displayName,
            content, Context.ConnectionAborted);

        switch (result.Status)
        {
            case SendStatus.Created:
                // Đẩy tới mọi kết nối trong phòng của nhóm, trên mọi bản chat-service (qua Redis Backplane).
                await Clients.Group(RoomName(groupId)).ReceiveMessage(result.Message!);
                break;
            case SendStatus.Duplicate:
                // Tin đã lưu và đã phát từ lần gửi trước → chỉ trả lại cho người gửi, không phát lại cho phòng.
                logger.LogInformation("Bỏ qua tin trùng {MessageId} (seq {Seq})", messageId, result.Message!.SequenceNumber);
                break;
            case SendStatus.IdTaken:
                throw new HubException("messageId đã được dùng cho tin khác.");
        }
        return result.Message!;
    }

    // Hỏi cache Redis group:members:{groupId} trước; cache trống mới gọi gRPC tới group-service.
    // Thêm/xóa thành viên → có hiệu lực sau ~1–2 giây (chờ sự kiện Kafka xóa cache).
    private async Task EnsureMemberAsync(Guid groupId)
    {
        var userId = Context.User!.GetUserId();
        bool isMember;
        try
        {
            isMember = await members.IsMemberAsync(groupId, userId, Context.ConnectionAborted);
        }
        catch (RpcException ex)
        {
            // group-service chết/chậm → không kiểm tra được quyền → TỪ CHỐI (an toàn hơn cho qua).
            logger.LogWarning("Không kiểm tra được thành viên nhóm {GroupId}: gRPC {Status} {Detail}",
                groupId, ex.StatusCode, ex.Status.Detail);
            throw new HubException($"Không kiểm tra được thành viên nhóm (group-service: {ex.StatusCode}). Thử lại sau.");
        }

        if (!isMember)
            throw new HubException("Bạn không phải thành viên nhóm này.");
    }
}
