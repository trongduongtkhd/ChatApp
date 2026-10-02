using System.ComponentModel.DataAnnotations;

namespace ChatApp.IdentityService.Dtos;

public record RegisterRequest(
    [Required, MinLength(3), MaxLength(50), RegularExpression("^[a-zA-Z0-9_.]+$",
        ErrorMessage = "UserName chỉ gồm chữ không dấu, số, '_' và '.'")]
    string UserName,

    [Required, EmailAddress, MaxLength(100)]
    string Email,

    [Required, MinLength(6), MaxLength(100)]
    string Password,

    [Required, MaxLength(100)]
    string DisplayName);
