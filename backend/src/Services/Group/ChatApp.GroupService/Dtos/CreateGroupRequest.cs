using System.ComponentModel.DataAnnotations;

namespace ChatApp.GroupService.Dtos;

public record CreateGroupRequest(
    [Required, MaxLength(100)] string Name,
    [MaxLength(500)] string? Description);
