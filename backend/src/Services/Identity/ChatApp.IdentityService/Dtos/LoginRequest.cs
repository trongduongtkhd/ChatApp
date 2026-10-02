using System.ComponentModel.DataAnnotations;

namespace ChatApp.IdentityService.Dtos;

public record LoginRequest(
    [Required] string UserName,
    [Required] string Password);
