param([switch]$SkipStartup,[switch]$SkipRegression)
$ErrorActionPreference='Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
function Step { param([string]$Name,[scriptblock]$Action) Write-Host "`n>>> $Name" -ForegroundColor Cyan; & $Action; if ($LASTEXITCODE -ne 0) { throw "$Name failed" } }
if (!$SkipStartup) { Step 'Build/start Chapter 8' { & powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-resilience-demo.ps1 } }
Step 'Real circuit, readiness, reconnect, backplane and CPU HPA' { node scripts/resilience-test.cjs }
Step 'Actual liveness process restart' { node scripts/k8s-probe-test.cjs }
Step 'Kubernetes source full backup/restore and artifact cleanup' { node scripts/backup-k8s-test.cjs }
Step 'Grafana, Seq and Angular browser regression' { node scripts/monitoring-ui-test.cjs }
if (!$SkipRegression) {
    try {
        Step 'Switch to Compose for existing container-specific regression tests' { & powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-resilience-demo.ps1 -SkipBuild -Mode Compose }
        Step 'Mongo/Presence/friends/week/reconnect regression' { & powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-chat-system.ps1 -SkipBuild }
        Step 'Admin/roles/revocation/dependency/UI regression' { & powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-admin-system.ps1 -SkipBuild -SkipRegression }
        Step 'Attachments API and UI' { node scripts/attachment-test.cjs --ui }
        Step 'Video signaling API and media UI' { node scripts/video-call-test.cjs --ui }
    } finally {
        Step 'Restore Kubernetes mode' { & powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-resilience-demo.ps1 -SkipBuild }
    }
}
Write-Host 'PASS: Chapter 8. Reports: infra/backups/runtime/chapter8' -ForegroundColor Green
