namespace ChatApp.GroupService.Dtos;

// Version: giá trị xmin hiện tại. Client phải gửi lại khi sửa nhóm (Optimistic Locking).
public record GroupDto(
    Guid Id,
    string Name,
    string? Description,
    Guid OwnerId,
    string MyRole,
    uint Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// UserName/DisplayName lấy từ user_snapshots; null nếu bản sao chưa đồng bộ (eventual consistency).
public record MemberDto(
    Guid UserId,
    string? UserName,
    string? DisplayName,
    string Role,
    DateTimeOffset JoinedAt);

public record GroupDetailDto(GroupDto Group, IReadOnlyList<MemberDto> Members);
