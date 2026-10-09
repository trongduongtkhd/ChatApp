using System.ComponentModel.DataAnnotations;

namespace ChatApp.IdentityService.Dtos;

public record SendFriendRequest([Required] Guid? UserId);

// DirectGroupId: mã nhóm chat riêng, identity tự tính bằng DirectChat.GroupIdFor (không hỏi group-service).
public record FriendDto(Guid UserId, string UserName, string DisplayName, DateTimeOffset Since, Guid DirectGroupId);

// UserId là người kia (người mời mình, hoặc người mình đã mời). SentAt: lần mời gần nhất.
public record FriendRequestDto(Guid UserId, string UserName, string DisplayName, DateTimeOffset SentAt);
