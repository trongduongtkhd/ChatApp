namespace ChatApp.ChatService.Services;

// Thu hồi phòng còn mở cả khi client ngừng heartbeat và Kafka đang gián đoạn.
// Chỉ kiểm quyền/snapshot, không gia hạn lease Presence thay cho client.
public sealed class MembershipRoomMonitor(PresenceCoordinator coordinator, ILogger<MembershipRoomMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await coordinator.RefreshInterestedAsync(); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "Chờ kiểm tra lại quyền phòng"); }
            try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
