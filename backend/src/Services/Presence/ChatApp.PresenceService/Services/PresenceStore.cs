using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatApp.PresenceService.Services;

// Lease từng ConnectionId, dùng Redis TIME để các tiến trình không so giờ máy riêng.
// Một Lua script cập nhật trạng thái + PUBLISH nguyên tử. Pub/Sub chỉ là tín hiệu:
// người nhận luôn đọc snapshot từ nguồn; heartbeat định kỳ sửa được thông báo bị lỡ.
public sealed class PresenceStore(IConnectionMultiplexer redis, IOptions<PresenceOptions> options)
{
    public const string ChangedChannel = "chatapp:presence:changed";
    private const string Prefix = "presence:v2:";
    private const string SessionScript = """
        local t = redis.call('TIME')
        local now = t[1]*1000 + math.floor(t[2]/1000)
        local key = KEYS[1]
        redis.call('ZREMRANGEBYSCORE', key, '-inf', now)
        if ARGV[3] == 'heartbeat' then
          redis.call('ZADD', key, now + tonumber(ARGV[4]), ARGV[2])
          redis.call('PEXPIRE', key, tonumber(ARGV[4])*2)
        else
          redis.call('ZREM', key, ARGV[2])
        end
        local first = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
        if #first == 0 then
          redis.call('ZREM', KEYS[2], ARGV[1])
          redis.call('DEL', key)
          if redis.call('SREM', KEYS[3], ARGV[1]) == 1 then
            redis.call('PUBLISH', ARGV[5], ARGV[1])
          end
        else
          redis.call('ZADD', KEYS[2], first[2], ARGV[1])
          if redis.call('SADD', KEYS[3], ARGV[1]) == 1 then
            redis.call('PUBLISH', ARGV[5], ARGV[1])
          end
        end
        return 1
        """;
    private const string SweepScript = """
        local t = redis.call('TIME')
        local now = t[1]*1000 + math.floor(t[2]/1000)
        local users = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', now, 'LIMIT', 0, 200)
        for _, user in ipairs(users) do
          local key = ARGV[1] .. user
          redis.call('ZREMRANGEBYSCORE', key, '-inf', now)
          local first = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
          if #first == 0 then
            redis.call('ZREM', KEYS[1], user)
            redis.call('DEL', key)
            if redis.call('SREM', KEYS[2], user) == 1 then
              redis.call('PUBLISH', ARGV[2], user)
            end
          else
            redis.call('ZADD', KEYS[1], first[2], user)
          end
        end
        return #users
        """;
    private const string OnlineScript = """
        local t = redis.call('TIME')
        local now = t[1]*1000 + math.floor(t[2]/1000)
        local result = {}
        for i, key in ipairs(KEYS) do
          if redis.call('ZCOUNT', key, '(' .. now, '+inf') > 0 then
            table.insert(result, ARGV[i])
          end
        end
        return result
        """;

    public Task HeartbeatAsync(Guid userId, string connectionId) => UpdateAsync(userId, connectionId, "heartbeat");
    public Task EndSessionAsync(Guid userId, string connectionId) => UpdateAsync(userId, connectionId, "end");

    private async Task UpdateAsync(Guid userId, string connectionId, string operation) =>
        await redis.GetDatabase().ScriptEvaluateAsync(SessionScript,
            [$"{Prefix}sessions:{userId}", $"{Prefix}due", $"{Prefix}online"],
            [userId.ToString(), connectionId, operation, options.Value.LeaseSeconds * 1000L, ChangedChannel]);

    public async Task<int> SweepAsync() => (int)await redis.GetDatabase().ScriptEvaluateAsync(
        SweepScript, [$"{Prefix}due", $"{Prefix}online"], [$"{Prefix}sessions:", ChangedChannel]);

    public async Task<IReadOnlyList<string>> GetOnlineAsync(IEnumerable<string> users)
    {
        var result = new List<string>();
        // Giới hạn độ dài script để không chiếm Redis lâu khi có nhiều bạn/nhóm.
        foreach (var batch in users.Distinct().Chunk(500))
        {
            var values = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(OnlineScript,
                batch.Select(u => (RedisKey)$"{Prefix}sessions:{u}").ToArray(),
                batch.Select(u => (RedisValue)u).ToArray()))!;
            result.AddRange(values.Select(v => v.ToString()));
        }
        return result;
    }
}
