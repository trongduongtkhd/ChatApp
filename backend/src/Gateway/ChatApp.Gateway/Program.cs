using ChatApp.Common.Operations;
using ChatApp.Common.Auth;
using ChatApp.Common.Backup;
using ChatApp.Gateway.Backup;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authorization;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddChatAppOperations("gateway");
builder.Services.AddHostedService<KafkaLagSampler>();
if (args.Contains("--backup-kafka"))
{
    if (!builder.Configuration.GetValue("Backup:RecoveryMode", false)) throw new InvalidOperationException("Kafka import only in recovery.");
    var directory = args[Array.IndexOf(args, "--directory") + 1];
    await KafkaBackup.ImportAsync(builder.Configuration["Kafka:BootstrapServers"]!, directory, CancellationToken.None);
    return;
}
var backupEnabled = builder.Configuration.GetValue("Backup:Enabled", true) && !builder.Configuration.GetValue("Backup:RecoveryMode", false);
builder.Services.AddRemoteAccountAccess();
builder.Services.AddSingleton<IAuthorizationHandler, BackupAdminHandler>();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379,abortConnect=false"));
builder.Services.AddSingleton<BackupOrchestrator>();
if (backupEnabled)
{
    var backupConnection = await BackupEndpoints.InitializeStorageAsync(builder.Configuration);
    builder.Services.AddHangfire(c => c.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(backupConnection)));
    builder.Services.AddHangfireServer(o => o.WorkerCount = 1);
}

// Cùng code kiểm tra JWT với các service (ChatApp.Common), cùng Jwt:Secret trong kho user-secrets chung.
builder.Services.AddChatAppJwtAuthentication(builder.Configuration);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("BackupAdmin", policy => policy.RequireAuthenticatedUser().AddRequirements(new BackupAdminRequirement()));
    // Lưới an toàn: route nào quên khai báo AuthorizationPolicy cũng mặc định phải đăng nhập.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// YARP đọc bảng Routes/Clusters từ mục "ReverseProxy" trong cấu hình.
// Mỗi Route có AuthorizationPolicy: "anonymous" (ai cũng vào) hoặc "default" (phải có JWT hợp lệ).
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();
app.UseChatAppOperations();
if (backupEnabled)
{
    app.Services.GetRequiredService<BackupOrchestrator>().ApplySchedule(app.Services.GetRequiredService<IRecurringJobManager>());
    app.MapBackupEndpoints();
}
else
{
    app.MapGet("/api/system/mode", () => new { recoveryMode = builder.Configuration.GetValue("Backup:RecoveryMode", false), backupId = builder.Configuration["Backup:RestoreId"] }).AllowAnonymous();
    app.MapGet("/api/admin/backups", () => Results.Ok(new { enabled = false, items = Array.Empty<object>(), settings = new BackupSettings(), recovery = new { status = "none" } })).RequireAuthorization("BackupAdmin");
}

// Xác thực (đọc + kiểm tra token) trước, phân quyền (route này có cho vào không) sau.
// Request bị chặn ở đây thì KHÔNG được chuyển tiếp tới service.
app.UseAuthentication();
app.UseAuthorization();

// Trả 413 ngay tại cửa vào. Nếu upstream từ chối khi YARP còn ghi body lớn,
// client có thể thấy 502 do request stream bị đóng thay vì lỗi dung lượng rõ ràng.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPut(context.Request.Method)
        && context.Request.Path.StartsWithSegments("/api/chat/groups")
        && context.Request.Path.Value!.Contains("/attachments/", StringComparison.Ordinal)
        && context.Request.ContentLength > 25L * 1024 * 1024)
    {
        context.Response.StatusCode = 413;
        await context.Response.WriteAsJsonAsync(new { title = "File không vượt 25 MiB." });
        return;
    }
    await next(context);
});

app.MapGet("/", () => "ChatApp.Gateway is running").AllowAnonymous();

// Mọi request khớp một Route sẽ được chuyển tiếp nguyên vẹn (method, header, body) tới service.
app.MapReverseProxy();

app.Run();
