namespace ChatApp.IdentityService.Dtos;

public record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
