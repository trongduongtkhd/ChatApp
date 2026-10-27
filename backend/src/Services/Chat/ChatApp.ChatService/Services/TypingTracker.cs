using System.Collections.Concurrent;

namespace ChatApp.ChatService.Services;

public sealed class TypingTracker
{
    private readonly ConcurrentDictionary<string, long> last = new();
    public bool Allow(string connectionId)
    {
        var now = Environment.TickCount64;
        if (last.TryGetValue(connectionId, out var previous) && now - previous < 1500) return false;
        last[connectionId] = now;
        return true;
    }
    public void Remove(string connectionId) => last.TryRemove(connectionId, out _);
}
