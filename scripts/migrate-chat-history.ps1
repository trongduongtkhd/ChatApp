param([switch]$Apply, [switch]$Verify)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
function Invoke-CheckedDocker {
    param([string[]]$DockerArgs)
    & docker @DockerArgs
    if ($LASTEXITCODE -ne 0) { throw "docker command failed (exit $LASTEXITCODE)" }
}
Invoke-CheckedDocker -DockerArgs @('compose','up','-d','mongo-init')
Invoke-CheckedDocker -DockerArgs @('wait','mongo-init')
$initExit = & docker inspect --format '{{.State.ExitCode}}' mongo-init
if ($initExit -ne '0') { throw 'mongo-init failed; xem docker compose logs mongo-init' }
Invoke-CheckedDocker -DockerArgs @('compose','build','chat-service-1')
if ($Apply) {
    Invoke-CheckedDocker -DockerArgs @('compose','stop','chat-service-1','chat-service-2')
    $backupName = 'chat-before-mongo-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.dump'
    $backupDir = Join-Path $projectRoot 'infra/backups'
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    $pgUser = (& docker exec postgres printenv POSTGRES_USER).Trim()
    Invoke-CheckedDocker -DockerArgs @('exec','postgres','pg_dump',"--username=$pgUser",'--dbname=chat_db','--format=custom',"--file=/tmp/$backupName")
    Invoke-CheckedDocker -DockerArgs @('cp',"postgres:/tmp/$backupName",(Join-Path $backupDir $backupName))
    Write-Host "Backup: infra/backups/$backupName"
    Invoke-CheckedDocker -DockerArgs @('compose','run','--rm','--no-deps','chat-migrate','--migrate-chat','--apply','--offline')
    Invoke-CheckedDocker -DockerArgs @('compose','run','--rm','--no-deps','chat-migrate','--migrate-chat','--verify')
    Invoke-CheckedDocker -DockerArgs @('compose','up','-d','chat-service-1','chat-service-2','nginx')
} else {
    $mode = if ($Verify) { '--verify' } else { '--dry-run' }
    Invoke-CheckedDocker -DockerArgs @('compose','run','--rm','--no-deps','chat-migrate','--migrate-chat',$mode)
}
