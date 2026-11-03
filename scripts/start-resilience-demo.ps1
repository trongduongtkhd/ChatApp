param([switch]$SkipBuild,[ValidateSet('Kubernetes','Compose')][string]$Mode='Kubernetes')
$ErrorActionPreference='Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
$runtime = Join-Path $taskRoot 'infra/backups/runtime/chapter8'
[IO.Directory]::CreateDirectory($runtime) | Out-Null
try { $chapterLock=New-Object IO.FileStream((Join-Path $runtime 'startup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch { throw 'Another Chapter 8 startup is running.' }
try {
if (!(Test-Path (Join-Path $runtime 'pod-targets.json'))) { [IO.File]::WriteAllText((Join-Path $runtime 'pod-targets.json'),'[]') }
function Checked { param([scriptblock]$Action) & $Action; if ($LASTEXITCODE -ne 0) { throw 'Chapter 8 command failed. See output above.' } }
# Quiesce the previous Chat runtime before starting the existing source mode.
$modeFile = Join-Path $runtime 'mode.json'
if ((Test-Path $modeFile) -and ((Get-Content -Raw $modeFile | ConvertFrom-Json).mode -eq 'kubernetes')) {
    Checked { kubectl --kubeconfig "$runtime/kubeconfig" -n chatapp delete hpa chat --ignore-not-found }
    Checked { kubectl --kubeconfig "$runtime/kubeconfig" -n chatapp scale deployment chat --replicas=0 }
    Checked { kubectl --kubeconfig "$runtime/kubeconfig" -n chatapp wait --for=delete pod -l app=chat --timeout=90s }
}
[IO.File]::WriteAllText($modeFile,'{"mode":"compose"}')
Checked { docker compose -f docker-compose.observability.yml up -d }
if ($SkipBuild) { Checked { & "$PSScriptRoot/start-backup-demo.ps1" -SkipBuild } }
else { Checked { & "$PSScriptRoot/start-backup-demo.ps1" } }
if ($Mode -eq 'Kubernetes') {
    Checked { node "$PSScriptRoot/setup-chat-k8s.cjs" }
    Checked { docker compose -f docker-compose.yml -f docker-compose.k8s.yml up -d --no-deps --force-recreate nginx }
    Checked { kubectl --kubeconfig "$runtime/kubeconfig" apply -f infra/k8s/hpa.yml }
} else { Checked { docker compose up -d --no-deps --force-recreate nginx } }
$agentScript = Join-Path $PSScriptRoot 'operations-agent.cjs'
$existing = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($agentScript) })
if (!$existing.Count) {
    Start-Process -FilePath (Get-Command node.exe).Source -ArgumentList @($agentScript) -WorkingDirectory $taskRoot -WindowStyle Hidden -RedirectStandardOutput "$runtime/agent.log" -RedirectStandardError "$runtime/agent.err.log" | Out-Null
}
Start-Sleep -Seconds 12
$health = Invoke-WebRequest 'http://localhost:5080/health/ready' -UseBasicParsing -TimeoutSec 15
if ($health.StatusCode -ne 200) { throw 'Chat is not ready.' }
Write-Host 'READY: ChatApp http://localhost:4200 | Grafana http://localhost:3000/d/chatapp-ch8 | Prometheus http://localhost:9090 | Seq http://localhost:5341' -ForegroundColor Green

} finally { $chapterLock.Dispose() }
