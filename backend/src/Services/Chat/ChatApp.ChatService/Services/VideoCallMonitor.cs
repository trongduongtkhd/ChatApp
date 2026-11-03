namespace ChatApp.ChatService.Services;

public sealed class VideoCallMonitor(VideoCallStore store, IServiceScopeFactory scopes, ILogger<VideoCallMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var iteration = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope(); var calls = scope.ServiceProvider.GetRequiredService<VideoCallCoordinator>();
                foreach (var ended in await store.SweepAsync()) await calls.NotifyAsync(ended);
                if (++iteration % 3 == 0)
                    foreach (var id in await store.ActiveAsync())
                    {
                        var call = await store.ReadAsync(id); if (call is null || call.State == "Ended") continue;
                        try { await calls.ValidateAsync(call, stoppingToken); }
                        catch (Exception) when (!stoppingToken.IsCancellationRequested) { await calls.ForceEndAsync(call, "permission-changed"); }
                    }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("Call monitor chờ phục hồi: {Error}", ex.Message); }
            try { await Task.Delay(2000, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
