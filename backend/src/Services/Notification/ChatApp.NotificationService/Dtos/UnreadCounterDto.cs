namespace ChatApp.NotificationService.Dtos;

// Số tin chưa đọc của user hiện tại trong một nhóm.
// LastReadSequence: Angular dùng để biết nên đánh dấu "tin mới" từ tin nào.
public sealed record UnreadCounterDto(Guid GroupId, int UnreadCount, long LastReadSequence);
