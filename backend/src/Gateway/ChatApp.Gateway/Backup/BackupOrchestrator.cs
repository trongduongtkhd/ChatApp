using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatApp.Common.Backup;
using Hangfire;
using StackExchange.Redis;

namespace ChatApp.Gateway.Backup;

public sealed record BackupFile(string Path, long Size, string Sha256);
public sealed record SystemBackupManifest(int Version, string Id, string PgTarget, uint MongoSeconds, uint MongoIncrement,
    double CheckpointMilliseconds, DateTimeOffset CreatedAt, Dictionary<string, string> Images, BackupFile[] Files);
public sealed record BackupSettings(string Period = "disabled", int RetentionCount = 5, int WalArchiveHours = 168);

public sealed class BackupOrchestrator(IConfiguration config, IConnectionMultiplexer redis, ILogger<BackupOrchestrator> logger)
{
    private readonly SemaphoreSlim recoveryRequests = new(1, 1);
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly string[] Participants = ["identity", "group", "notification"];
    private static readonly Regex IdPattern = new("^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant);
    public string ProjectRoot { get; } = FindProjectRoot(config["Backup:ProjectRoot"]);
    public string Root => Path.Combine(ProjectRoot, "infra", "backups", "system");
    public bool Enabled => config.GetValue("Backup:Enabled", true) && !config.GetValue("Backup:RecoveryMode", false);
    public string DirectoryFor(string id)
    { if (!IdPattern.IsMatch(id)) throw new ArgumentException("Mã bản sao lưu không hợp lệ."); return Path.Combine(Root, id); }
    public string NewId() => DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8];
    public BackupSettings Settings => File.Exists(Path.Combine(Root, "settings.json"))
        ? JsonSerializer.Deserialize<BackupSettings>(File.ReadAllText(Path.Combine(Root, "settings.json")), Json)! : new();
    public async Task SaveSettingsAsync(BackupSettings settings, IRecurringJobManager manager)
    {
        if (settings.Period is not ("disabled" or "hourly" or "daily") || settings.RetentionCount is < 1 or > 30 || settings.WalArchiveHours is < 24 or > 720) throw new ArgumentException("Lịch hoặc chính sách lưu giữ không hợp lệ.");
        await WriteJsonAsync(Path.Combine(Root, "settings.json"), settings);
        ApplySchedule(manager, settings);
    }
    public void ApplySchedule(IRecurringJobManager manager, BackupSettings? settings = null)
    {
        settings ??= Settings;
        if (!Enabled || settings.Period == "disabled") { manager.RemoveIfExists("chatapp-system-backup"); return; }
        manager.AddOrUpdate<BackupOrchestrator>("chatapp-system-backup", service => service.ScheduledAsync(),
            settings.Period == "hourly" ? "0 * * * *" : "0 2 * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh") });
    }
    public object[] List() => Directory.Exists(Root) ? Directory.GetDirectories(Root).Where(d => IdPattern.IsMatch(Path.GetFileName(d)))
        .OrderByDescending(d => d).Select(d => ReadJson(Path.Combine(d, "state.json"))).Where(x => x.HasValue).Select(x => (object)x!.Value).ToArray() : [];
    public object RecoveryState => (object?)ReadJson(Path.Combine(Root, "recovery.json")) ?? new { status = "none" };
    public async Task<string> PrepareAsync(string actor)
    {
        if (!Enabled) throw new InvalidOperationException("Sao lưu bị tắt trong môi trường phục hồi.");
        var id = NewId(); Directory.CreateDirectory(DirectoryFor(id));
        await StateAsync(id, "queued", "Đang chờ thực hiện", actor);
        return id;
    }
    [AutomaticRetry(Attempts = 0)]
    public async Task ScheduledAsync() { var id = await PrepareAsync("schedule"); await CreateAsync(id, "schedule"); }

    [AutomaticRetry(Attempts = 0)]
    public async Task CreateAsync(string id, string actor)
    {
        if (!Enabled) throw new InvalidOperationException("Backup disabled.");
        var directory = DirectoryFor(id);
        var chatRuntime = new ChatBackupRuntime(ProjectRoot);
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = timeout.Token;
        await using var jobLock = new FileStream(Path.Combine(Root, ".worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stage = "/tmp/chatapp-full-" + id;
        var token = Guid.NewGuid().ToString("N");
        var db = redis.GetDatabase();
        try
        {
            if (File.Exists(Path.Combine(directory, "manifest.json"))) { await VerifyAsync(id, ct); await StateAsync(id, "complete", "Đã kiểm tra lại bản sao lưu", actor); return; }
            await StateAsync(id, "running", "Tạo bản nền PostgreSQL; ứng dụng tiếp tục phục vụ", actor);
            await CheckParticipantsAsync(chatRuntime, ct);
            var images = new Dictionary<string, string>();
            foreach (var pair in new Dictionary<string,string> { ["identity"]="chatapp/identity-service", ["group"]="chatapp/group-service", ["notification"]="chatapp/notification-service", ["gateway"]="chatapp/gateway", ["presence"]="chatapp/presence-service", ["chat"]="chatapp/chat-service" })
                images[pair.Key] = (await RunAsync(directory, "docker.exe", ["image", "inspect", "--format", "{{.Id}}", pair.Value], ct)).Trim();
            var webRoot = Path.Combine(ProjectRoot, "frontend", "chat-app", "dist", "chat-app");
            if (!File.Exists(Path.Combine(webRoot, "index.html"))) throw new InvalidOperationException("Cần build Angular production trước khi sao lưu.");
            foreach (var asset in Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories))
            {
                if (File.GetAttributes(asset).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Web asset must not be a symlink.");
                var destination = Path.Combine(directory, "web", Path.GetRelativePath(webRoot, asset));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(asset, destination);
            }
            await RunAsync(directory, "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/backup-postgres.ps1", "-Destination", Path.Combine(directory, "postgres")], ct);
            await StateAsync(id, "running", "Chốt mốc phối hợp dữ liệu", actor);
            var target = "system_" + id.Replace('-', '_');
            var watch = Stopwatch.StartNew();
            KafkaBackup.Checkpoint kafka;
            uint seconds, increment;
            string pgLsn;
            if (!await db.StringSetAsync(BackupWriteGate.IntentKey, token, TimeSpan.FromSeconds(8), When.NotExists)) throw new InvalidOperationException("Một mốc sao lưu khác đang được chốt.");
            try
            {
                using var cutTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cutTimeout.CancelAfter(TimeSpan.FromSeconds(7));
                var cutCt = cutTimeout.Token;
                var epoch = await db.StringGetAsync(BackupWriteGate.EpochKey);
                var generations = await CheckParticipantsAsync(chatRuntime, cutCt);
                while (true)
                {
                    cutCt.ThrowIfCancellationRequested();
                    var ready = true;
                    foreach (var instance in generations)
                        if (await db.StringGetAsync(BackupWriteGate.AckPrefix + instance.Key) != instance.Value + ":" + token) ready = false;
                    if (ready) break;
                    await Task.Delay(50, cutCt);
                }
                // Both markers and consumer offsets are captured while app writers
                // AND publishers/consumers have drained, not while copying files.
                pgLsn = (await RunAsync(directory, "docker.exe", ["exec", "--user", "postgres", "postgres", "psql", "-XAt", "-U", config["Backup:PostgresUser"] ?? "chatapp", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", $"SELECT pg_create_restore_point('{target}');"], cutCt)).Trim();
                await RunAsync(directory, chatRuntime.Executable, chatRuntime.Exec(["dotnet", "ChatApp.ChatService.dll", "--backup-data", "checkpoint", "--directory", stage, "--id", id]), cutCt);
                await RunAsync(directory, chatRuntime.Executable, chatRuntime.Copy(stage + "/mongo-point.json", Path.Combine(directory, "mongo-point.json")), cutCt);
                var point = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "mongo-point.json"), cutCt));
                seconds = point.RootElement.GetProperty("seconds").GetUInt32(); increment = point.RootElement.GetProperty("increment").GetUInt32();
                kafka = KafkaBackup.Capture(config["Backup:Kafka"] ?? "localhost:9092");
                var after = await CheckParticipantsAsync(chatRuntime, cutCt);
                if (await db.StringGetAsync(BackupWriteGate.IntentKey) != token || await db.StringGetAsync(BackupWriteGate.EpochKey) != epoch || generations.Count != after.Count || generations.Any(p => !after.TryGetValue(p.Key, out var v) || v != p.Value))
                    throw new InvalidOperationException("Mốc bị gián đoạn hoặc service đổi phiên; bộ backup không được đánh dấu thành công.");
            }
            finally { await ReleaseIntentAsync(db, token); }
            watch.Stop();
            // No application write gate is held beyond this point.
            await StateAsync(id, "running", "Lưu WAL, snapshot MongoDB và nội dung ảnh/file", actor);
            await SealPgPointAsync(directory, target, pgLsn, ct);
            await RunAsync(directory, chatRuntime.Executable, chatRuntime.Exec(["dotnet", "ChatApp.ChatService.dll", "--backup-data", "export", "--directory", stage, "--seconds", seconds.ToString(), "--increment", increment.ToString()]), ct);
            Directory.CreateDirectory(Path.Combine(directory, "mongo"));
            await RunAsync(directory, chatRuntime.Executable, chatRuntime.Copy(stage + "/.", Path.Combine(directory, "mongo")), ct);
            await KafkaBackup.ExportAsync(config["Backup:Kafka"] ?? "localhost:9092", kafka, Path.Combine(directory, "kafka"), ct);
            // Redis holds runtime state (presence/calls/gates), not message truth.
            // AOF protects Redis restart; capture RDB for audit, do not resurrect sessions.
            await RunAsync(directory, "docker.exe", ["exec", "redis", "redis-cli", "-e", "BGSAVE", "SCHEDULE"], ct);
            while ((await RunAsync(directory, "docker.exe", ["exec", "redis", "redis-cli", "INFO", "persistence"], ct)).Contains("rdb_bgsave_in_progress:1")) await Task.Delay(200, ct);
            await RunAsync(directory, "docker.exe", ["cp", "redis:/data/dump.rdb", Path.Combine(directory, "redis-runtime.rdb")], ct);
            await StateAsync(id, "running", "Kiểm tra toàn vẹn bộ phục hồi", actor);
            var files = new List<BackupFile>();
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order())
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (relative is "state.json" or "worker.log" or "manifest.json" || relative.EndsWith(".tmp")) continue;
                await using var input = File.OpenRead(file);
                files.Add(new(relative, input.Length, Convert.ToHexString(await SHA256.HashDataAsync(input, ct))));
            }
            await WriteJsonAsync(Path.Combine(directory, "manifest.json"), new SystemBackupManifest(1, id, target, seconds, increment, watch.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow, images, files.ToArray()));
            await VerifyAsync(id, ct);
            await StateAsync(id, "complete", "Đã sao lưu đầy đủ và kiểm tra checksum", actor, checkpointMs: watch.Elapsed.TotalMilliseconds);
            try { await PruneAsync(); await PruneArchiveAsync(directory, ct); } catch (Exception ex) { logger.LogWarning(ex, "Backup complete; retention cleanup could not finish"); }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "System backup {Id} failed", id);
            await StateAsync(id, "failed", ex.GetBaseException().Message, actor);
            throw;
        }
        finally
        {
            await ReleaseIntentAsync(db, token);
            try { await RunAsync(directory, chatRuntime.Executable, chatRuntime.Exec(["rm", "-rf", "--", stage]), CancellationToken.None); } catch (Exception ex) { logger.LogWarning("Could not remove own backup staging: {Error}", ex.Message); }
        }
    }

    public async Task VerifyAsync(string id, CancellationToken ct)
    {
        var directory = DirectoryFor(id);
        var manifest = JsonSerializer.Deserialize<SystemBackupManifest>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), ct), Json)!;
        if (manifest.Version != 1 || manifest.Id != id || manifest.Files.Length == 0) throw new InvalidOperationException("Manifest không hợp lệ.");
        foreach (var file in manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(directory, file.Path));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidOperationException("Thiếu file backup hoặc đường dẫn không hợp lệ.");
            await using var input = File.OpenRead(path);
            if (input.Length != file.Size || Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != file.Sha256) throw new InvalidOperationException("Checksum không khớp: " + file.Path);
        }
    }
    [AutomaticRetry(Attempts = 0)]
    public async Task RestoreAsync(string id, string actor)
    {
        Directory.CreateDirectory(Root);
        await using var jobLock = new FileStream(Path.Combine(Root, ".worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            await WriteJsonAsync(Path.Combine(Root, "recovery.json"), new { id, status = "running", actor, message = "Kiểm tra và dựng bản phục hồi riêng", url = (string?)null });
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await VerifyAsync(id, timeout.Token);
            await RunAsync(DirectoryFor(id), "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/restore-system.ps1", "-BackupId", id], timeout.Token);
            await WriteJsonAsync(Path.Combine(Root, "recovery.json"), new { id, status = "ready", actor, message = "Bản phục hồi đã sẵn sàng", url = "http://localhost:4201" });
        }
        catch (Exception ex) { await WriteJsonAsync(Path.Combine(Root, "recovery.json"), new { id, status = "failed", actor, message = ex.GetBaseException().Message, url = (string?)null }); throw; }
    }
    public async Task DeleteRecoveryAsync()
    {
        await using var jobLock = new FileStream(Path.Combine(Root, ".worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await RunAsync(Root, "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/restore-system.ps1", "-Cleanup"], CancellationToken.None);
        await WriteJsonAsync(Path.Combine(Root, "recovery.json"), new { status = "none" });
    }
    public async Task PrepareRecoveryAsync(string id, string actor)
    {
        DirectoryFor(id);
        await recoveryRequests.WaitAsync();
        try
        {
            if (!File.Exists(Path.Combine(DirectoryFor(id), "manifest.json"))) throw new InvalidOperationException("Bản sao lưu chưa hoàn tất hoặc không tồn tại.");
            var previous = ReadJson(Path.Combine(Root, "recovery.json"));
            if (previous is { } state && state.GetProperty("status").GetString() is "queued" or "running" or "ready")
                throw new InvalidOperationException("Dọn môi trường phục hồi hiện tại trước khi tạo bản khác.");
            await WriteJsonAsync(Path.Combine(Root, "recovery.json"), new { id, status = "queued", actor, message = "Đang chờ dựng bản phục hồi", url = (string?)null });
        }
        finally { recoveryRequests.Release(); }
    }
    private async Task PruneArchiveAsync(string directory, CancellationToken ct)
    {
        // Completed bundles contain their own WAL. This only removes old copies
        // from the archive spool, never pg_wal or the standby replication slot.
        await RunAsync(directory, "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/prune-postgres-archive.ps1", "-RetentionHours", Settings.WalArchiveHours.ToString()], ct);
    }
    private async Task SealPgPointAsync(string directory, string target, string lsn, CancellationToken ct)
    {
        // This script records the ALREADY created marker; it must not create a
        // later marker after releasing the cross-service barrier.
        await RunAsync(directory, "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/seal-system-postgres.ps1", "-BackupDirectory", Path.Combine(directory, "postgres"), "-TargetName", target, "-TargetLsn", lsn], ct);
    }
    private async Task<Dictionary<string, string>> CheckParticipantsAsync(ChatBackupRuntime runtime, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        var expected = Participants.Concat(await runtime.ParticipantsAsync(ct)).ToArray();
        foreach (var name in expected)
        {
            ct.ThrowIfCancellationRequested();
            var value = await redis.GetDatabase().StringGetAsync(BackupWriteGate.InstancePrefix + name);
            if (!value.HasValue) throw new InvalidOperationException("Service chưa sẵn sàng cho backup: " + name);
            result[name] = value.ToString();
        }
        // Reject live writers from another runtime; stale names expire through their heartbeat keys.
        foreach (var registered in await redis.GetDatabase().SetMembersAsync(BackupWriteGate.RegistryKey))
            if (!result.ContainsKey(registered.ToString()) && await redis.GetDatabase().KeyExistsAsync(BackupWriteGate.InstancePrefix + registered))
                throw new InvalidOperationException("Untracked writer is still active: " + registered);
        return result;
    }
    private static async Task ReleaseIntentAsync(IDatabase db, string token)
    { try { await db.ScriptEvaluateAsync("if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end", [BackupWriteGate.IntentKey], [token]); } catch { /* TTL releases a lost coordinator. */ } }
    private async Task PruneAsync()
    {
        var pinned = ReadJson(Path.Combine(Root, "recovery.json"));
        var pin = pinned is { } p && p.TryGetProperty("id", out var pinProperty) ? pinProperty.GetString() : null;
        var completed = Directory.GetDirectories(Root).Where(d => IdPattern.IsMatch(Path.GetFileName(d)))
            .Where(d => ReadJson(Path.Combine(d, "state.json")) is { } s && s.GetProperty("status").GetString() == "complete").OrderByDescending(d => d).ToArray();
        foreach (var old in completed.Skip(Settings.RetentionCount))
        {
            var id = Path.GetFileName(old);
            if (id == pin || old != DirectoryFor(id) || File.GetAttributes(old).HasFlag(FileAttributes.ReparsePoint)) continue;
            await VerifyAsync(id, CancellationToken.None);
            Directory.Delete(old, true); // only validated child directories created by this system
        }
    }
    private Task StateAsync(string id, string status, string message, string actor, double? checkpointMs = null) =>
        WriteJsonAsync(Path.Combine(DirectoryFor(id), "state.json"), new { id, status, message, actor, updatedAt = DateTimeOffset.UtcNow, checkpointMs });
    public static async Task WriteJsonAsync(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, true);
    }
    public static JsonElement? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone() : null; }
        catch (IOException) { return null; }
    }
    private static string FindProjectRoot(string? configured)
    {
        if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
        for (var parent = new DirectoryInfo(Directory.GetCurrentDirectory()); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "CLAUDE.md"))) return parent.FullName;
        return Directory.GetCurrentDirectory();
    }
    private async Task<string> RunAsync(string directory, string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = ProjectRoot, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start backup tool.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch { try { process.Kill(true); } catch { } throw; }
        var output = await stdout; var error = await stderr;
        await File.AppendAllTextAsync(Path.Combine(directory, "worker.log"), output + error, ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Công cụ {executable} thất bại: " + (error.Length > 0 ? error : output)[..Math.Min(1600, (error.Length > 0 ? error : output).Length)]);
        return output;
    }
}
