namespace ChatApp.ChatService.Dtos;

// Tin nhắn gửi cho client (qua SignalR ReceiveMessage và REST lịch sử). JSON tự đổi sang camelCase.
// Client sắp xếp theo SequenceNumber, KHÔNG theo CreatedAt.
public record MessageDto(
    Guid Id,
    Guid GroupId,
    Guid SenderId,
    string SenderName,
    string Content,
    long SequenceNumber,
    DateTimeOffset CreatedAt);
