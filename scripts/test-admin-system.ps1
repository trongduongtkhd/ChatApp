param([switch]$SkipBuild, [switch]$SkipRegression)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
function Invoke-AdminStep {
    param([string]$Label, [scriptblock]$Action)
    Write-Host "`n>>> $Label" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Label failed (exit $LASTEXITCODE)" }
}
$suiteStarted = (Get-Date).ToUniversalTime().ToString('o')
$fixturesStarted = $false
try {
if (!$SkipBuild) {
    Invoke-AdminStep 'Build backend' { dotnet build backend/ChatApp.sln --nologo -v minimal }
    $env:NG_CLI_ANALYTICS = 'false'
    Push-Location frontend/chat-app
    try { Invoke-AdminStep 'Angular 12 production' { npm run build -- --configuration production } } finally { Pop-Location }
}
# Tuần tự: các test fault tự dừng/khởi động service, không chạy song song.
$fixturesStarted = $true
Invoke-AdminStep 'Admin API, roles, approval, invites and revocation' { node scripts/admin-test.cjs }
Invoke-AdminStep 'Admin and group UI' { node scripts/admin-ui-test.cjs }
Invoke-AdminStep 'Identity/Group outage and recovery' { node scripts/admin-dependency-test.cjs }
if (!$SkipRegression) { Invoke-AdminStep 'Work 1 + 2 regression' { & powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-chat-system.ps1 -SkipBuild } }
Write-Host "`nPASS: admin system" -ForegroundColor Green
} finally {
    if ($fixturesStarted) {
        Invoke-AdminStep 'Clean fixtures created by this test run' { node scripts/cleanup-test-data.cjs --apply --wait "--since=$suiteStarted" }
    }
}
