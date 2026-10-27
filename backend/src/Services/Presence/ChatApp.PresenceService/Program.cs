using ChatApp.PresenceService.Grpc;
using ChatApp.PresenceService.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<PresenceOptions>().BindConfiguration("Presence")
    .Validate(o => o.HeartbeatSeconds >= 2 && o.LeaseSeconds >= o.HeartbeatSeconds * 3
        && o.LeaseSeconds <= 3600 && o.SweepSeconds >= 1 && o.SweepSeconds <= o.LeaseSeconds,
        "Lease phải >= 3 chu kỳ heartbeat; cấu hình thời gian không hợp lệ").ValidateOnStart();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(
    builder.Configuration.GetConnectionString("Redis") ?? throw new InvalidOperationException("Thiếu Redis")));
builder.Services.AddSingleton<PresenceStore>();
builder.Services.AddHostedService<PresenceSweeper>();
builder.Services.AddGrpc();
var app = builder.Build();
app.MapGrpcService<PresenceGrpcService>().RequireHost("*:5015");
app.MapGet("/health", async (IConnectionMultiplexer redis) =>
{
    try { await redis.GetDatabase().PingAsync(); return Results.Ok(new { status = "healthy" }); }
    catch { return Results.StatusCode(503); }
}).RequireHost("*:5005");
app.Run();
