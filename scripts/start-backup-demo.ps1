param([switch]$SkipBuild)
# Preserve the selected Chapter 8 runtime when reusing the Chapter 7 entry point.
$selectedModeFile=Join-Path (Split-Path -Parent $PSScriptRoot) 'infra/backups/runtime/chapter8/mode.json'
if ((Test-Path $selectedModeFile) -and ((Get-Content -Raw $selectedModeFile | ConvertFrom-Json).mode -eq 'kubernetes')) {
    if ($SkipBuild) { & "$PSScriptRoot/start-resilience-demo.ps1" -SkipBuild }
    else { & "$PSScriptRoot/start-resilience-demo.ps1" }
    exit $LASTEXITCODE
}
. "$PSScriptRoot/postgres-backup-common.ps1"
Set-Location -LiteralPath $script:PgBackupProjectRoot
$runtime = Join-Path $script:PgBackupProjectRoot 'infra/backups/runtime'
[IO.Directory]::CreateDirectory($runtime) | Out-Null
$startupLog = Join-Path $runtime 'startup.log'
try { $startupLock = New-Object IO.FileStream((Join-Path $runtime 'startup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch [IO.IOException] { throw 'Another ChatApp startup is running. Wait for it to finish instead of running this command again.' }
try {
[IO.File]::WriteAllText($startupLog, "ChatApp startup $(Get-Date -Format o)`r`n")
function Write-StartupStep {
    param([string]$Message)
    $script:startupStep = $Message
    Write-Host "`n$Message" -ForegroundColor Cyan
    Add-Content -LiteralPath $startupLog -Value $Message -Encoding UTF8
}
function Invoke-StartupDocker {
    param([string[]]$DockerArgs)
    # Stream build/start progress instead of hiding it until Docker finishes.
    $docker = (Get-Command docker.exe -CommandType Application | Select-Object -First 1).Source
    $previousPreference = $ErrorActionPreference
    try { $ErrorActionPreference = 'Continue'; & $docker @DockerArgs; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previousPreference }
    if ($code -ne 0) { throw "Docker failed (exit $code). See the error above; startup has not completed." }
}
function Wait-HttpReady {
    param([string]$Name,[string]$Uri,[Diagnostics.Process]$Process,[int]$TimeoutSeconds = 180)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process) {
            $Process.Refresh()
            if ($Process.HasExited) { throw "$Name exited before it was ready. Check $runtime\$Name.err.log and $runtime\$Name.log" }
        }
        try {
            $response = Invoke-WebRequest -Uri $Uri -Headers @{Accept="text/html"} -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { Write-Host "Ready: $Name"; return }
        } catch { }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for $Name at $Uri. Check logs in $runtime" }
        Start-Sleep -Milliseconds 500
    } while ($true)
}
Write-StartupStep '[1/8] Checking Node 14, Docker and local build files...'
if ((& node --version) -notmatch '^v14\.') { throw 'Use Node 14 for Angular 12.' }
Invoke-PgDocker @('info','--format','{{.ServerVersion}}') | Out-Null
if ($SkipBuild -and !(Test-Path -LiteralPath 'frontend/chat-app/dist/chat-app/index.html')) { throw 'Angular build is missing. Run this script without -SkipBuild.' }
function Invoke-CheckedTool {
    param([scriptblock]$Action)
    & $Action
    if ($LASTEXITCODE -ne 0) { throw 'Build/start command failed.' }
}
function Stop-OwnedLocalService {
    param([string]$Name,[string]$Directory,[int]$Port)
    $exe = Join-Path (Join-Path $script:PgBackupProjectRoot $Directory) "bin/Debug/net10.0/ChatApp.$Name.exe"
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
    foreach ($processId in @($listeners | ForEach-Object { $_.OwningProcess } | Select-Object -Unique)) {
        $existing = Get-CimInstance Win32_Process -Filter "ProcessId=$processId"
        if (!$existing.ExecutablePath -or ![string]::Equals($existing.ExecutablePath,$exe,[StringComparison]::OrdinalIgnoreCase)) { throw "Port $Port is owned by an unrelated process. Close that process yourself." }
        Stop-Process -Id $processId -Force
    }
}
if (!$SkipBuild) {
    Write-StartupStep '[2/8] Building .NET backend...'
    # Running Windows apphosts hold their DLLs open; stop only this project's
    # known local services before rebuilding changed code.
    Stop-OwnedLocalService 'IdentityService' 'backend/src/Services/Identity/ChatApp.IdentityService' 5001
    Stop-OwnedLocalService 'GroupService' 'backend/src/Services/Group/ChatApp.GroupService' 5002
    Stop-OwnedLocalService 'NotificationService' 'backend/src/Services/Notification/ChatApp.NotificationService' 5004
    Stop-OwnedLocalService 'Gateway' 'backend/src/Gateway/ChatApp.Gateway' 5000
    Invoke-CheckedTool { dotnet build backend/ChatApp.sln --nologo -v minimal }
    Write-StartupStep '[3/8] Building Angular. Web is not available until startup finishes...'
    Push-Location frontend/chat-app
    try {
        if ((& node --version) -notmatch '^v14\.') { throw 'Use Node 14 for Angular 12.' }
        Invoke-CheckedTool { npm run build -- --configuration production }
    } finally { Pop-Location }
    Write-StartupStep '[4/8] Building Docker images. Progress is shown below; keep this window open...'
    Invoke-StartupDocker @('compose','--progress','plain','build','chat-service-1','presence-service','backup-image-identity','backup-image-group','backup-image-notification','backup-image-gateway')
} else {
    Write-StartupStep '[2-4/8] Reusing existing backend, Angular and Docker builds (-SkipBuild).'
}
Write-StartupStep '[5/8] Starting databases, Kafka, Redis and Chat containers...'
Invoke-StartupDocker @('compose','up','-d','postgres','redis','kafka','kafka-init','mongo-init','attachment-storage','presence-service','chat-service-1','chat-service-2','nginx')
& "$PSScriptRoot/setup-postgres-replica.ps1"

function Start-LocalService {
    param([string]$Name,[string]$Directory,[int]$Port)
    $projectDirectory = Join-Path $script:PgBackupProjectRoot $Directory
    $exe = Join-Path $projectDirectory "bin/Debug/net10.0/ChatApp.$Name.exe"
    Stop-OwnedLocalService $Name $Directory $Port
    $previousEnv = $env:ASPNETCORE_ENVIRONMENT
    $previousUrls = $env:ASPNETCORE_URLS
    try {
        $env:ASPNETCORE_ENVIRONMENT='Development'; $env:ASPNETCORE_URLS="http://localhost:$Port"
        $process = Start-Process -FilePath $exe -WorkingDirectory $projectDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runtime "$Name.log") -RedirectStandardError (Join-Path $runtime "$Name.err.log")
        Write-Host "$Name PID $($process.Id), port $Port"
        Wait-HttpReady -Name $Name -Uri "http://localhost:$Port/" -Process $process
    } finally { $env:ASPNETCORE_ENVIRONMENT=$previousEnv; $env:ASPNETCORE_URLS=$previousUrls }
}
Write-StartupStep '[6/8] Starting local backend services and waiting for their HTTP endpoints...'
Start-LocalService 'IdentityService' 'backend/src/Services/Identity/ChatApp.IdentityService' 5001
Start-LocalService 'GroupService' 'backend/src/Services/Group/ChatApp.GroupService' 5002
Start-LocalService 'NotificationService' 'backend/src/Services/Notification/ChatApp.NotificationService' 5004
Start-LocalService 'Gateway' 'backend/src/Gateway/ChatApp.Gateway' 5000
Write-StartupStep '[7/8] Starting web on port 4200...'
$listeners = @(Get-NetTCPConnection -State Listen -LocalPort 4200 -ErrorAction SilentlyContinue)
if ($listeners.Count -eq 0) {
    $node = (Get-Command node.exe -CommandType Application | Select-Object -First 1).Source
    $webProcess = Start-Process -FilePath $node -ArgumentList @((Join-Path $script:PgBackupProjectRoot 'scripts/serve-backup-web.cjs')) -WorkingDirectory $script:PgBackupProjectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runtime 'web.log') -RedirectStandardError (Join-Path $runtime 'web.err.log')
    Wait-HttpReady -Name 'web' -Uri 'http://localhost:4200/auth/login' -Process $webProcess
} else {
    Wait-HttpReady -Name 'existing web' -Uri 'http://localhost:4200/auth/login'
}
Write-StartupStep '[8/8] Checking Chat replicas, Presence and web-to-Gateway proxy...'
Wait-HttpReady -Name 'Chat replica 1' -Uri 'http://localhost:5003/'
Wait-HttpReady -Name 'Chat replica 2' -Uri 'http://localhost:5013/'
Wait-HttpReady -Name 'Presence' -Uri 'http://localhost:5005/health'
$mode = Invoke-WebRequest -Uri 'http://localhost:4200/api/system/mode' -UseBasicParsing -TimeoutSec 10
if (($mode.Content | ConvertFrom-Json).recoveryMode) { throw 'Port 4200 is serving a recovery environment, not the source app.' }
Write-StartupStep 'READY: http://localhost:4200/auth/login'
Write-Host 'Startup finished. You can now open the web. Admin > Sao luu & phuc hoi.' -ForegroundColor Green
} catch {
    $failure = "FAILED at $script:startupStep`r`n$($_.Exception.Message)`r`nRuntime logs: $runtime"
    Add-Content -LiteralPath $startupLog -Value $failure -Encoding UTF8
    Write-Error $failure -ErrorAction Continue
    exit 1
} finally { $startupLock.Dispose() }
