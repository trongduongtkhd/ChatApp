using System.Diagnostics;
using System.Text.Json;

namespace ChatApp.Gateway.Backup;

// One snapshot of runtime selection per backup. Existing Compose recovery stays unchanged.
public sealed class ChatBackupRuntime(string root)
{
    private string Config => Path.Combine(root, "infra", "backups", "runtime", "chapter8", "mode.json");
    private string KubeConfig => Path.Combine(root, "infra", "backups", "runtime", "chapter8", "kubeconfig");
    public bool Kubernetes { get; } = File.Exists(Path.Combine(root, "infra", "backups", "runtime", "chapter8", "mode.json")) &&
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "infra", "backups", "runtime", "chapter8", "mode.json"))).RootElement.GetProperty("mode").GetString() == "kubernetes";
    public string? SelectedPod { get; private set; }
    public async Task<string[]> ParticipantsAsync(CancellationToken ct)
    {
        if (!Kubernetes) return ["chat-1", "chat-2"];
        using var document = JsonDocument.Parse(await KubeAsync(["-n", "chatapp", "get", "pods", "-l", "app=chat", "-o", "json"], ct));
        var pods = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var names = pods.Where(p => !p.GetProperty("metadata").TryGetProperty("deletionTimestamp", out _))
            .Select(p => p.GetProperty("metadata").GetProperty("name").GetString()!).Order().ToArray();
        if (names.Length < 2 || names.Length > 4) throw new InvalidOperationException("Chat Pod set is not stable for backup.");
        SelectedPod ??= names[0];
        if (!names.Contains(SelectedPod)) throw new InvalidOperationException("Backup export Pod changed.");
        return names;
    }
    public string[] Exec(string[] args) => Kubernetes
        ? ["--kubeconfig", KubeConfig, "-n", "chatapp", "exec", SelectedPod!, "--", ..args]
        : ["exec", "chat-service-1", ..args];
    public string[] Copy(string source, string destination) => Kubernetes
        ? ["--kubeconfig", KubeConfig, "-n", "chatapp", "cp", SelectedPod + ":" + source, "./" + Path.GetRelativePath(root, destination).Replace('\\', '/')]
        : ["cp", "chat-service-1:" + source, destination];
    public string Executable => Kubernetes ? "kubectl.exe" : "docker.exe";
    private async Task<string> KubeAsync(string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("kubectl.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--kubeconfig"); start.ArgumentList.Add(KubeConfig);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); } catch { try { process.Kill(true); } catch { } throw; }
        var result = await output; var detail = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot inspect Kubernetes writers: " + detail);
        return result;
    }
}
