using ChatApp.Common.Auth;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace ChatApp.Gateway.Backup;

public sealed class BackupAdminRequirement : IAuthorizationRequirement;
public sealed class BackupAdminHandler(IAccountAccessReader reader) : AuthorizationHandler<BackupAdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, BackupAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        try
        {
            var id = context.User.GetUserId();
            var account = (await reader.ReadAsync([id], CancellationToken.None)).GetValueOrDefault(id);
            if (AccountAccessRules.Allows(context.User, account) && account!.SystemRole == AccountAccessRules.SystemAdmin) context.Succeed(requirement);
        }
        catch { /* Cannot verify current role/account version: fail closed. */ }
    }
}

public static class BackupEndpoints
{
    public static async Task<string> InitializeStorageAsync(IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("BackupDb") ?? configuration.GetConnectionString("IdentityDb")
            ?? throw new InvalidOperationException("Cấu hình ConnectionStrings:BackupDb trong user-secrets để bật sao lưu.");
        var builder = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres" };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname='backup_db'", admin);
        if ((long)(await exists.ExecuteScalarAsync())! == 0)
        { await using var create = new NpgsqlCommand("CREATE DATABASE backup_db", admin); await create.ExecuteNonQueryAsync(); }
        builder.Database = "backup_db";
        return builder.ConnectionString;
    }

    public static void MapBackupEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/mode", (IConfiguration c) => new { recoveryMode = c.GetValue("Backup:RecoveryMode", false), backupId = c["Backup:RestoreId"] }).AllowAnonymous();
        var api = app.MapGroup("/api/admin/backups").RequireAuthorization("BackupAdmin");
        api.MapGet("", (BackupOrchestrator service) => Results.Ok(new { enabled = service.Enabled, items = service.List(), settings = service.Settings, recovery = service.RecoveryState }));
        api.MapPost("", async (BackupOrchestrator service, IBackgroundJobClient jobs, HttpContext context) =>
        {
            if (!service.Enabled) return Results.Conflict(new { title = "Đây là môi trường phục hồi; không tạo backup tại đây." });
            var actor = context.User.GetUserId().ToString();
            var id = await service.PrepareAsync(actor);
            jobs.Enqueue<BackupOrchestrator>(worker => worker.CreateAsync(id, actor));
            return Results.Accepted(value: new { id });
        });
        api.MapPut("/settings", async (BackupSettings settings, BackupOrchestrator service, IRecurringJobManager manager) =>
        {
            if (!service.Enabled) return Results.Conflict(new { title = "Không đổi lịch trong môi trường phục hồi." });
            try { await service.SaveSettingsAsync(settings, manager); return Results.Ok(settings); }
            catch (ArgumentException ex) { return Results.BadRequest(new { title = ex.Message }); }
        });
        api.MapPost("/{id}/verify", async (string id, BackupOrchestrator service, CancellationToken ct) =>
        {
            try { await service.VerifyAsync(id, ct); return Results.Ok(new { verified = true }); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Results.BadRequest(new { title = ex.Message }); }
        });
        api.MapPost("/{id}/restore", async (string id, BackupOrchestrator service, IBackgroundJobClient jobs, HttpContext context) =>
        {
            if (!service.Enabled) return Results.Conflict(new { title = "Không khởi tạo phục hồi từ môi trường phục hồi." });
            try { service.DirectoryFor(id); }
            catch (ArgumentException ex) { return Results.BadRequest(new { title = ex.Message }); }
            var actor = context.User.GetUserId().ToString();
            try { await service.PrepareRecoveryAsync(id, actor); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { title = ex.Message }); }
            jobs.Enqueue<BackupOrchestrator>(worker => worker.RestoreAsync(id, actor));
            return Results.Accepted(value: new { id });
        });
        api.MapDelete("/recovery", async (BackupOrchestrator service) =>
        {
            if (!service.Enabled) return Results.Conflict(new { title = "Chỉ dọn bản phục hồi từ web nguồn." });
            try { await service.DeleteRecoveryAsync(); return Results.Ok(new { status = "none" }); }
            catch (IOException) { return Results.Conflict(new { title = "Một tác vụ đang chạy. Đợi hoàn tất rồi thử lại." }); }
        });
    }
}
