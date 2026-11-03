using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ChatApp.Common.Backup;

// A short distributed checkpoint barrier, NOT a lock held while copying a backup.
// Each process closes its LOCAL admission gate, drains its active writers, then ACKs.
// An ACK is bound to an instance generation and the checkpoint token. Redis TTLs
// release a dead coordinator; missing participants/ACKs make the backup fail closed.
public sealed class BackupWriteGate(IConfiguration config, ILogger<BackupWriteGate> logger) : BackgroundService
{
    public const string IntentKey = "chatapp:backup:intent";
    public const string InstancePrefix = "chatapp:backup:instance:";
    public const string AckPrefix = "chatapp:backup:ack:";
    public const string EpochKey = "chatapp:backup:membership-epoch";
    public const string RegistryKey = "chatapp:backup:instances";
    private readonly bool enabled = config.GetValue("Backup:CoordinationEnabled", false);
    private readonly string name = config["Backup:InstanceId"] ?? Environment.MachineName;
    private readonly string generation = Guid.NewGuid().ToString("N");
    private readonly object sync = new();
    private TaskCompletionSource opened = NewSignal();
    private bool open;
    private int writers;
    private ConnectionMultiplexer? redis;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task PulseAsync()
    {
        if (!enabled) return;
        redis ??= await ConnectionMultiplexer.ConnectAsync(config.GetConnectionString("Redis") ?? "localhost:6379,abortConnect=false");
        var db = redis.GetDatabase();
        // Registration and observing intent must be atomic: a new Pod cannot start
        // writing between joining the registry and noticing a checkpoint barrier.
        var observed = await db.ScriptEvaluateAsync("""
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then redis.call('INCR', KEYS[3]) end
            redis.call('SET', KEYS[1], ARGV[1], 'EX', 10)
            redis.call('SADD', KEYS[4], ARGV[2])
            return redis.call('GET', KEYS[2]) or ''
            """, [InstancePrefix + name, IntentKey, EpochKey, RegistryKey], [generation, name]);
        var intent = observed.ToString();
        bool drained;
        lock (sync)
        {
            if (!string.IsNullOrEmpty(intent))
            {
                if (open) { open = false; opened = NewSignal(); }
            }
            else if (!open) { open = true; opened.TrySetResult(); }
            drained = !open && writers == 0;
        }
        if (drained)
            await db.StringSetAsync(AckPrefix + name, generation + ":" + intent, TimeSpan.FromSeconds(10));
    }

    // Called before startup migrations/index changes as well as before HTTP starts.
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!enabled) return;
        do
        {
            await PulseAsync();
            lock (sync) { if (open) return; }
            await Task.Delay(100, ct);
        } while (!ct.IsCancellationRequested);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!enabled) return;
        while (!ct.IsCancellationRequested)
        {
            try { await PulseAsync(); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lock (sync) { if (open) { open = false; opened = NewSignal(); } }
                logger.LogWarning("Backup coordination unavailable: {Error}", ex.Message);
            }
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        if (!enabled) return new Lease(null);
        while (true)
        {
            Task wait;
            lock (sync)
            {
                if (open) { writers++; return new Lease(this); }
                wait = opened.Task;
            }
            await wait.WaitAsync(ct);
        }
    }

    private sealed class Lease(BackupWriteGate? gate) : IDisposable
    {
        private BackupWriteGate? owner = gate;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is not null) lock (current.sync) current.writers--;
        }
    }

    public override void Dispose() { redis?.Dispose(); base.Dispose(); }
}

public sealed class BackupWriteMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, BackupWriteGate gate)
    {
        // Internal gRPC methods read account/membership state. Gating nested RPCs
        // would deadlock a writer already admitted by another service.
        var request = context.Request;
        if (request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true ||
            request.Path.StartsWithSegments("/hubs") || HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        { await next(context); return; }
        IDisposable lease;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(9));
        try { lease = await gate.EnterAsync(timeout.Token); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { title = "Đang chốt mốc sao lưu. Vui lòng thử lại." }); return; }
        using (lease) await next(context);
    }
}

public sealed class BackupHubFilter(BackupWriteGate gate) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocation, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (invocation.HubMethodName is not ("SendMessage" or "SendMessageWithAttachments")) return await next(invocation);
        IDisposable lease;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(invocation.Context.ConnectionAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(9));
        try { lease = await gate.EnterAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new HubException("Đang chốt mốc sao lưu. Gửi lại với cùng mã tin nhắn."); }
        using (lease) return await next(invocation);
    }
}

public static class BackupWriteExtensions
{
    public static IServiceCollection AddBackupWriteCoordination(this IServiceCollection services)
    {
        services.AddSingleton<BackupWriteGate>();
        services.AddHostedService(p => p.GetRequiredService<BackupWriteGate>());
        services.AddSingleton<BackupHubFilter>();
        return services;
    }
    public static async Task InitializeBackupWriteCoordinationAsync(this WebApplication app) =>
        await app.Services.GetRequiredService<BackupWriteGate>().InitializeAsync();
}
