using ChatApp.ChatService.Dtos;
using ChatApp.ChatService.Services;
using ChatApp.Common.Auth;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;

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
    PresenceCoordinator presence,
    TypingTracker typing,
    ChatApp.ChatService.Grpc.GroupMembershipClient groupSource,
    ILogger<ChatHub> logger) : Hub<IChatClient>
{
    private const int MaxContentLength = 4000;

    // "Phòng" SignalR của một nhóm chat. Tên phòng = groupId.
    // Phòng chỉ là danh sách ConnectionId do SignalR giữ trong bộ nhớ của bản chat-service này.
    // Redis Backplane (Program.cs) làm cho lệnh gửi tới phòng có hiệu lực trên MỌI bản: mỗi bản đẩy cho kết nối của mình.
    public static string RoomName(Guid groupId) => groupId.ToString();


    // SignalR gọi khi một kết nối mới mở xong (đã qua [Authorize]).
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.GetUserId();
        presence.Connect(Context.ConnectionId, userId);
        // Không chờ Presence: service này hỏng không được làm kết nối chat thất bại.
        logger.LogInformation("User {UserId} kết nối {ConnectionId}", userId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    // SignalR gọi khi kết nối đóng: client tự đóng, mất mạng (hết thời gian chờ ping), hoặc server tắt đúng cách.
    // Nếu tiến trình bị kill, Presence Service vẫn dọn lease. Transport mất không thu hồi ngay
    // để người dùng có thời gian reconnect; logout chủ động gọi EndSession.
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User!.GetUserId();
        presence.Disconnect(Context.ConnectionId);
        typing.Remove(Context.ConnectionId);
        logger.LogInformation("User {UserId} ngắt {ConnectionId}; chờ lease hết hạn nếu không reconnect", userId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // Vào phòng của nhóm để bắt đầu nhận ReceiveMessage. Chỉ thành viên mới được vào (nếu không, người ngoài nghe lén được).
    // PresenceSnapshot độc lập với phòng đang mở, được cập nhật bởi Heartbeat.
    public async Task JoinGroup(Guid groupId)
    {
        await EnsureMemberAsync(groupId);
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomName(groupId));
        presence.TrackRoom(Context.ConnectionId, groupId);

        var userId = Context.User!.GetUserId();
        logger.LogInformation("User {UserId} (connection {ConnectionId}) vào phòng {GroupId}",
            userId, Context.ConnectionId, groupId);

    }

    // Rời phòng: không nhận tin của nhóm nữa (vd chuyển sang mở nhóm khác). Không cần kiểm tra thành viên.
    public async Task LeaveGroup(Guid groupId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomName(groupId));
        presence.LeaveRoom(Context.ConnectionId, groupId);
    }

    public Task Heartbeat() => presence.HeartbeatAsync(Context.ConnectionId);

    public async Task EndSession()
    {
        await presence.EndAsync(Context.ConnectionId);
        Context.Abort();
    }

    public async Task SetTyping(Guid groupId, bool isTyping)
    {
        if (!presence.InRoom(Context.ConnectionId, groupId))
            throw new HubException("Bạn chưa mở cuộc trò chuyện này.");
        if (isTyping && !typing.Allow(Context.ConnectionId)) return;
        // Đọc nguồn quyền hiện tại, không dùng cache thành viên cũ cho chức năng mới.
        var allowed = await groupSource.IsMemberAsync(groupId, Context.User!.GetUserId(), Context.ConnectionAborted);
        if (!allowed) throw new HubException("Bạn không còn là thành viên.");
        var name = Context.User!.GetDisplayName();
        await Clients.OthersInGroup(RoomName(groupId)).TypingChanged(groupId, Context.User!.GetUserId(), name, isTyping);
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
        SendMessageResult result;
        try
        {
            result = await messages.SendAsync(messageId, groupId, user.GetUserId(),
                string.IsNullOrEmpty(displayName) ? user.GetUserName() : displayName,
                content, Context.ConnectionAborted);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            logger.LogWarning("Chưa xác nhận lưu tin {MessageId}: {Error}", messageId, ex.Message);
            throw new HubException("Chưa xác nhận lưu tin. Hãy thử gửi lại cùng tin nhắn.");
        }

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

    // Công việc 3: hỏi quyền hiện tại từ Group gRPC, không chờ Kafka xóa cache.
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
