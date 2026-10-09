namespace ChatApp.GroupService.Dtos;

// Version: giá trị xmin hiện tại. Client phải gửi lại khi sửa nhóm (Optimistic Locking).
// IsDirect + Peer (Phần 11): chat riêng thì Peer là người kia (giao diện hiện tên người kia thay cho Name rỗng);
// nhóm thường Peer = null.
public record GroupDto(
    Guid Id,
    string Name,
    string? Description,
    Guid OwnerId,
    string MyRole,
    uint Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsDirect,
    PeerDto? Peer);

// UserName/DisplayName null nếu bản sao user_snapshots chưa có người này (eventual consistency).
public record PeerDto(Guid UserId, string? UserName, string? DisplayName);

// UserName/DisplayName lấy từ user_snapshots; null nếu bản sao chưa đồng bộ (eventual consistency).
public record MemberDto(
    Guid UserId,
    string? UserName,
    string? DisplayName,
    string Role,
    DateTimeOffset JoinedAt);

public record GroupDetailDto(GroupDto Group, IReadOnlyList<MemberDto> Members);
