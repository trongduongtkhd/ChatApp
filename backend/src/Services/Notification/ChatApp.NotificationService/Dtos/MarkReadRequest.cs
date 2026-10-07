namespace ChatApp.NotificationService.Dtos;

// Body của POST /api/notifications/groups/{groupId}/read.
// LastReadSequence: sequenceNumber của tin mới nhất user đã thấy (Angular gửi seq lớn nhất đang hiển thị).
// long? để phân biệt "không gửi" (null → 400) với gửi 0.
public sealed record MarkReadRequest(long? LastReadSequence);
