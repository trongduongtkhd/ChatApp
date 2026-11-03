using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;
using Serilog;
using Serilog.Events;

namespace ChatApp.Common.Operations;

// Chapter 8: bounded labels only. Never put user/group/message IDs or tokens in metrics.
public static class OperationsMetrics
{
    public static readonly Gauge Connections = Metrics.CreateGauge("chatapp_signalr_connections", "Local SignalR connections.");
    public static readonly Counter Calls = Metrics.CreateCounter("chatapp_hub_calls_total", "Hub results.", new CounterConfiguration { LabelNames = ["method", "result"] });
    public static readonly Histogram Duration = Metrics.CreateHistogram("chatapp_hub_duration_seconds", "Server hub method duration, not browser end-to-end latency.", new HistogramConfiguration { LabelNames = ["method"], Buckets = Histogram.ExponentialBuckets(.005, 2, 14) });
    public static readonly Counter DependencyCalls = Metrics.CreateCounter("chatapp_dependency_calls_total", "Logical RPC results.", new CounterConfiguration { LabelNames = ["dependency", "result"] });
    public static readonly Counter Retries = Metrics.CreateCounter("chatapp_dependency_retries_total", "Transient RPC retries.", new CounterConfiguration { LabelNames = ["dependency"] });
    public static readonly Gauge Circuit = Metrics.CreateGauge("chatapp_circuit_state", "0 closed, 1 open, 2 half-open.", new GaugeConfiguration { LabelNames = ["dependency"] });
    public static readonly Gauge Pending = Metrics.CreateGauge("chatapp_outbox_pending", "Unpublished outbox records.");
    public static readonly Gauge Ready = Metrics.CreateGauge("chatapp_ready", "Readiness check: 1 ready, 0 not ready.");
}

public sealed class OperationsHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        using var timing = OperationsMetrics.Duration.WithLabels(context.HubMethodName).NewTimer();
        try { var value = await next(context); OperationsMetrics.Calls.WithLabels(context.HubMethodName, "success").Inc(); return value; }
        catch { OperationsMetrics.Calls.WithLabels(context.HubMethodName, "failure").Inc(); throw; }
    }
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    { await next(context); context.Context.Items["chatapp.operations.counted"] = true; OperationsMetrics.Connections.Inc(); }
    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    { try { await next(context, exception); } finally { if (context.Context.Items.Remove("chatapp.operations.counted")) OperationsMetrics.Connections.Dec(); } }
}

public sealed record ReadinessProbe(Func<IServiceProvider, CancellationToken, Task<bool>> Check);
public sealed class ApplicationReadiness(IServiceScopeFactory scopes, ReadinessProbe probe, Microsoft.Extensions.Configuration.IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        try
        {
            var maintenance = config["Operations:ReadinessFile"];
            var ready = (maintenance is null || !File.Exists(maintenance)) && await probe.Check(scope.ServiceProvider, cancellationToken);
            OperationsMetrics.Ready.Set(ready ? 1 : 0);
            return ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Required storage is unavailable.");
        }
        catch { OperationsMetrics.Ready.Set(0); return HealthCheckResult.Unhealthy("Required storage is unavailable."); }
    }
}

// Runs independently of probes so readiness is also visible on the monitoring dashboard.
public sealed class ReadinessSampler(IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try { await scope.ServiceProvider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains("ready"), timeout.Token); } catch { OperationsMetrics.Ready.Set(0); }
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

public static class OperationsExtensions
{
    public static void AddChatAppOperations(this WebApplicationBuilder builder, string service,
        Func<IServiceProvider, CancellationToken, Task<bool>>? ready = null)
    {
        var instance = builder.Configuration["Backup:InstanceId"] ?? Environment.MachineName;
        Metrics.DefaultRegistry.SetStaticLabels(new Dictionary<string, string> { ["service"] = service, ["instance"] = instance });
        builder.Host.UseSerilog((_, _, log) => log.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext().Enrich.WithProperty("Service", service).Enrich.WithProperty("Instance", instance)
            .WriteTo.Console().WriteTo.Seq(builder.Configuration["Operations:SeqUrl"] ?? "http://localhost:5341"));
        builder.Services.AddSingleton(new ReadinessProbe(ready ?? ((_, _) => Task.FromResult(true))));
        builder.Services.AddHealthChecks().AddCheck<ApplicationReadiness>("required-storage", tags: ["ready"], timeout: TimeSpan.FromSeconds(3));
        builder.Services.AddHostedService<ReadinessSampler>();
        builder.Services.AddSingleton<GrpcResilience>();
        builder.Services.AddSingleton<OperationsHubFilter>();
        builder.Services.Configure<HubOptions>(o => o.AddFilter<OperationsHubFilter>());
    }
    public static void UseChatAppOperations(this WebApplication app)
    {
        // Query strings may contain SignalR tokens. Only log the path, never the query/body.
        app.UseSerilogRequestLogging(o => o.EnrichDiagnosticContext = (log, ctx) =>
            log.Set("TraceId", Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier));
        app.UseHttpMetrics();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = (ctx, report) => ctx.Response.WriteAsJsonAsync(new { status = report.Status.ToString(), checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()) })
        }).AllowAnonymous();
        app.MapMetrics().AllowAnonymous();
    }
}
