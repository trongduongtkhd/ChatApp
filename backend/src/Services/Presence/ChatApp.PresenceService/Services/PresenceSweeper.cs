using Microsoft.Extensions.Options;

namespace ChatApp.PresenceService.Services;

public sealed class PresenceSweeper(PresenceStore store, IOptions<PresenceOptions> options,
    ILogger<PresenceSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SweepSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Mỗi lượt có giới hạn, không dùng KEYS/SCAN toàn bộ Redis.
                await store.SweepAsync();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Không quét được lease presence; sẽ thử lại");
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
