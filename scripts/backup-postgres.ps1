param(
    [string]$SourceContainer = 'postgres',
    [ValidateRange(0,1048576)][int]$MaxRateKb = 0,
    [string]$Destination = ''
)
. "$PSScriptRoot/postgres-backup-common.ps1"
$source = Get-PgSource $SourceContainer
if ((Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql 'SHOW archive_mode;') -ne 'on') {
    throw 'archive_mode must be on. Apply the PostgreSQL Compose configuration first.'
}
if ($MaxRateKb -gt 0 -and $MaxRateKb -lt 32) { throw 'MaxRateKb must be 0 or at least 32.' }
$backupId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$directory = if ($Destination) { Get-PgBackupDirectory $Destination } else { Join-Path (Get-PgBackupRoot) $backupId }
if (Test-Path -LiteralPath $directory) { throw 'Backup destination already exists.' }
[IO.Directory]::CreateDirectory((Join-Path $directory 'base')) | Out-Null
$stage = '/tmp/chatapp-pg-backup-' + [Guid]::NewGuid().ToString('N')
$started = [DateTime]::UtcNow.ToString('o')
Write-PgBackupJson -Path (Join-Path $directory 'metadata.partial.json') -Value @{ status='in-progress'; startedAtUtc=$started }
try {
    Write-Host "Creating online base backup: $backupId"
    $arguments = @('exec', '--user', 'postgres', $source.Container, 'pg_basebackup',
        '--host', '/var/run/postgresql', '--username', $source.DbUser, '--no-password',
        '--pgdata', $stage, '--format=plain', '--wal-method=stream', '--checkpoint=fast', '--manifest-checksums=SHA256')
    if ($MaxRateKb -gt 0) { $arguments += "--max-rate=$MaxRateKb" }
    Invoke-PgDocker $arguments | Out-Null
    Invoke-PgDocker @('exec', '--user', 'postgres', $source.Container, 'pg_verifybackup', $stage) | Out-Null
    Invoke-PgDocker @('cp', "$($source.Container):$stage/.", (Join-Path $directory 'base')) | Out-Null
    # Close the current WAL segment and wait for the actual archived file.
    $walFile = Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql 'SELECT pg_walfile_name(pg_switch_wal());'
    Wait-PgArchivedWal -Source $source -WalFile $walFile
    Save-PgArchivedWal -Source $source -BackupDirectory $directory -ThroughWal $walFile | Out-Null
    if ((Invoke-PgDocker @('inspect', '--format', '{{.State.StartedAt}}', $source.Container)) -ne $source.StartedAt) {
        throw 'Source restarted during backup; the backup is not marked complete.'
    }
    $metadata = [ordered]@{
        schemaVersion=1; status='complete'; scope='postgres-only'; backupId=$backupId
        startedAtUtc=$started; completedAtUtc=[DateTime]::UtcNow.ToString('o')
        sourceContainer=$source.Container; sourceStartedAt=$source.StartedAt
        systemIdentifier=$source.SystemId; timeline=$source.Timeline; postgresMajor=17; imageId=$source.ImageId; dbUser=$source.DbUser
        baseManifestSha256=(Get-FileHash -LiteralPath (Join-Path $directory 'base/backup_manifest') -Algorithm SHA256).Hash
        recoveryPoints=@()
    }
    Write-PgBackupJson -Path (Join-Path $directory 'metadata.json') -Value $metadata
    Remove-Item -LiteralPath (Join-Path $directory 'metadata.partial.json')
    Write-Host "PASS: base verified; source stayed running. Backup: $directory" -ForegroundColor Green
    return [pscustomobject]@{ BackupId=$backupId; BackupDirectory=$directory }
} finally {
    Remove-PgBackupStage -Container $source.Container -Stage $stage
}
