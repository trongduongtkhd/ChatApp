namespace ChatApp.Contracts.Events;

// Topic chat.message-sent, key = GroupId (3 partition: tin cùng nhóm luôn vào cùng partition → giữ thứ tự).
// chat phát, notification nghe để tăng số tin chưa đọc (Phần 9).
// ContentPreview: 50 ký tự đầu, đủ để hiện thông báo, không gửi cả nội dung dài qua Kafka.
public sealed record MessageSent(
    Guid MessageId,
    Guid GroupId,
    Guid SenderId,
    string SenderName,
    long SequenceNumber,
    string ContentPreview) : IntegrationEvent
{
    public override string EventType => nameof(MessageSent);
}
