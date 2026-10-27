using ChatApp.Common.Auth;

namespace ChatApp.ChatService.Services;

public sealed class AccountRevocationHandler(PresenceCoordinator presence) : IAccountRevocationHandler
{
    public Task RevokeAsync(string connectionId) => presence.EndAsync(connectionId);
}
