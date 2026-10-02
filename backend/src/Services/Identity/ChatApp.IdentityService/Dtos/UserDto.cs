namespace ChatApp.IdentityService.Dtos;

// Dữ liệu user trả ra ngoài: KHÔNG có PasswordHash.
public record UserDto(Guid Id, string UserName, string Email, string DisplayName, DateTimeOffset CreatedAt);
