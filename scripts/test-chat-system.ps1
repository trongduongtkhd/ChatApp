param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
function Invoke-Step {
    param([string]$Label, [scriptblock]$Action)
    Write-Host "`n>>> $Label" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Label failed (exit $LASTEXITCODE)" }
}
$suiteStarted = (Get-Date).ToUniversalTime().ToString('o')
$fixturesStarted = $false
try {
# Không chạy các bước tắt/restart container song song.
if (!$SkipBuild) {
    Invoke-Step 'Build Chat backend' { dotnet build backend/src/Services/Chat/ChatApp.ChatService --nologo -v minimal }
    Push-Location frontend/chat-app
    try { Invoke-Step 'Angular 12 production' { npm run build -- --configuration production } } finally { Pop-Location }
}
Invoke-Step 'Verify legacy PostgreSQL -> Mongo' { docker compose run --rm --no-deps chat-migrate --migrate-chat --verify }
$fixturesStarted = $true
Invoke-Step 'Mongo persistence, two shards, week index, transactions' { node scripts/mongo-chat-test.cjs }
Invoke-Step 'Mongo/Kafka/shard outages' { node scripts/mongo-chat-test.cjs --faults }
Invoke-Step 'Mongo counter recovery' { dotnet run scripts/chat-test.cs -- seqlost }
Invoke-Step 'Friends and private chat regression' { dotnet run scripts/chat-test.cs -- friends }
Invoke-Step 'Presence multiple sessions, 60s expiry, typing' { dotnet run scripts/chat-test.cs -- presence }
Invoke-Step 'SIGKILL, offline Kafka and REST recovery' { dotnet run scripts/chat-test.cs -- presencekill }
Invoke-Step 'Angular end-to-end: presence + week + unread + reconnect' { node scripts/presence-ui-test.cjs }
Write-Host "`nPASS: work 1 + 2" -ForegroundColor Green
} finally {
    if ($fixturesStarted) {
        Invoke-Step 'Clean fixtures created by this test run' { node scripts/cleanup-test-data.cjs --apply --wait "--since=$suiteStarted" }
    }
}
