using System.ComponentModel.DataAnnotations;

namespace ChatApp.GroupService.Dtos;

// Version: giá trị version client nhận được lúc đọc nhóm. Nếu nhóm đã bị sửa sau đó → 409.
public record UpdateGroupRequest(
    [Required, MaxLength(100)] string Name,
    [MaxLength(500)] string? Description,
    [Required] uint? Version);
