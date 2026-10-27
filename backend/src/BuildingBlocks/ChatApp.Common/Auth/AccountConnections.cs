using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatApp.Common.Auth;

public interface IAccountRevocationHandler { Task RevokeAsync(string connectionId); }

public sealed class AccountConnections(IServiceScopeFactory scopes, ILogger<AccountConnections> logger)
{
    private readonly ConcurrentDictionary<string, HubCallerContext> connections = new();
    public void Track(HubCallerContext context) => connections[context.ConnectionId] = context;
    public void Remove(string id) => connections.TryRemove(id, out _);
    public HubCallerContext[] Snapshot() => connections.Values.ToArray();
    public async Task RevokeAsync(HubCallerContext context)
    {
        if (!connections.TryRemove(context.ConnectionId, out _)) return;
        using var scope = scopes.CreateScope();
        try
        {
            if (scope.ServiceProvider.GetService<IAccountRevocationHandler>() is { } handler)
                await handler.RevokeAsync(context.ConnectionId);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Không dọn được lease khi thu hồi connection"); }
        finally { context.Abort(); }
    }
}

public sealed class AccountHubFilter(IServiceScopeFactory scopes, AccountConnections connections) : IHubFilter
{
    private async Task<bool> Allowed(HubCallerContext context)
    {
        using var scope = scopes.CreateScope();
        var id = context.User!.GetUserId();
        var state = (await scope.ServiceProvider.GetRequiredService<IAccountAccessReader>()
            .ReadAsync(new[] { id }, context.ConnectionAborted)).GetValueOrDefault(id);
        return AccountAccessRules.Allows(context.User!, state);
    }
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        if (!await Allowed(context.Context)) { context.Context.Abort(); throw new HubException("Phiên đăng nhập không còn hiệu lực."); }
        connections.Track(context.Context);
        try { await next(context); } catch { connections.Remove(context.Context.ConnectionId); throw; }
    }
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        bool allowed;
        try { allowed = await Allowed(context.Context); }
        catch (Exception) when (!context.Context.ConnectionAborted.IsCancellationRequested)
        { throw new HubException("Chưa kiểm tra được tài khoản. Thử lại sau."); }
        if (!allowed)
        {
            await connections.RevokeAsync(context.Context);
            throw new HubException("Phiên đăng nhập không còn hiệu lực.");
        }
        return await next(context);
    }
    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        connections.Remove(context.Context.ConnectionId);
        await next(context, exception);
    }
}

// Không phụ thuộc heartbeat client hay Kafka: chặn cả connection chỉ nghe, không gửi gì.
public sealed class AccountConnectionMonitor(AccountConnections connections, IServiceScopeFactory scopes,
    ILogger<AccountConnectionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = connections.Snapshot();
                using var scope = scopes.CreateScope();
                var reader = scope.ServiceProvider.GetRequiredService<IAccountAccessReader>();
                foreach (var batch in snapshot.Select(c => c.User!.GetUserId()).Distinct().Chunk(500))
                {
                    IReadOnlyDictionary<Guid, AccountAccessState> states;
                    try { states = await reader.ReadAsync(batch, stoppingToken); }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning(ex, "Không xác nhận được quyền connection; thu hồi theo fail closed"); states = new Dictionary<Guid, AccountAccessState>(); }
                    var ids = batch.ToHashSet();
                    await Parallel.ForEachAsync(snapshot.Where(c => ids.Contains(c.User!.GetUserId()) &&
                        !AccountAccessRules.Allows(c.User!, states.GetValueOrDefault(c.User!.GetUserId()))),
                        new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = stoppingToken },
                        async (context, _) => await connections.RevokeAsync(context));
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "Account monitor chờ phục hồi"); }
            try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
