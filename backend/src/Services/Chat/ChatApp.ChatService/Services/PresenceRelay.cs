using StackExchange.Redis;

namespace ChatApp.ChatService.Services;

// Kênh tín hiệu được coalescing vào một slot: nếu bận chỉ cần đồng bộ trạng thái mới nhất.
// Heartbeat định kỳ là đường sửa sai khi Pub/Sub bị gián đoạn.
public sealed class PresenceRelay(IConnectionMultiplexer redis, PresenceCoordinator coordinator,
    ILogger<PresenceRelay> logger) : BackgroundService
{
    public const string MembershipChannel = "chatapp:membership:changed";
    private readonly System.Threading.Channels.Channel<bool> signals =
        System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
        { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sub = redis.GetSubscriber();
        var presenceChannel = RedisChannel.Literal("chatapp:presence:changed");
        var membershipChannel = RedisChannel.Literal(MembershipChannel);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await sub.SubscribeAsync(presenceChannel, (_, _) => signals.Writer.TryWrite(true));
                    await sub.SubscribeAsync(membershipChannel, (_, _) => signals.Writer.TryWrite(true));
                    break;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Chưa đăng ký được kênh presence; thử lại sau 2s");
                    await Task.Delay(2000, stoppingToken);
                }
            }
            await foreach (var signal in signals.Reader.ReadAllAsync(stoppingToken))
            {
                try { await coordinator.RefreshInterestedAsync(); }
                catch (Exception ex) { logger.LogWarning(ex, "Không đồng bộ được presence"); }
            }
        }
        finally
        {
            await sub.UnsubscribeAsync(presenceChannel);
            await sub.UnsubscribeAsync(membershipChannel);
        }
    }
}
