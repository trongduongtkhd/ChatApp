param(
    [Parameter(Mandatory=$true)][string]$BackupDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$')][string]$TargetName,
    [ValidatePattern('^chatapp-pg-restore(?:-[a-z0-9][a-z0-9-]{0,31})?$')][string]$RestoreName = 'chatapp-pg-restore',
    [ValidateRange(5,300)][int]$TimeoutSeconds = 60
)
. "$PSScriptRoot/postgres-backup-common.ps1"
$directory = Get-PgBackupDirectory $BackupDirectory
$metadata = Get-PgBackupMetadata $directory
$points = @($metadata.recoveryPoints | Where-Object { $_.name -eq $TargetName })
if ($points.Count -ne 1) { throw 'Target name must be a sealed recovery point in this backup.' }
$point = $points[0]
if ($point.lsn -notmatch '^[0-9A-F]+/[0-9A-F]+$') { throw 'Invalid recovery point LSN.' }
if (@($point.walFiles).Count -eq 0) { throw 'No archived WAL registered for this recovery point.' }
foreach ($file in $point.walFiles) {
    if ($file.name -notmatch '^([0-9A-F]{24}(\.[0-9A-F]{8}\.backup)?|[0-9A-F]{8}\.history)$') { throw 'Invalid archived WAL filename.' }
    $path = Join-Path (Join-Path $directory 'wal') $file.name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing archived WAL: $($file.name)" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Archived WAL checksum mismatch: $($file.name)" }
}
$volumeName = "$RestoreName-data"
$prepareName = "$RestoreName-prepare"
if ((Test-PgContainerExists $RestoreName) -or (Test-PgContainerExists $prepareName) -or (Test-PgVolumeExists $volumeName)) {
    throw 'Restore container/volume already exists. Choose another name or explicitly clean the previous restore.'
}
$settingsName = "recovery-$RestoreName.conf"
$settings = @"

# Isolated PostgreSQL-only PITR; never connect this instance to application consumers.
restore_command = 'cp "/backup/wal/%f" "%p"'
recovery_target_name = '$TargetName'
recovery_target_action = 'pause'
recovery_target_timeline = 'current'
primary_conninfo = ''
primary_slot_name = ''
"@
[IO.File]::WriteAllText((Join-Path $directory $settingsName), $settings.Replace("`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
$owned = $false
try {
    Invoke-PgDocker @('volume', 'create', '--label', "chatapp.backup.restore=$RestoreName", $volumeName) | Out-Null
    $owned = $true
    $dataMount = "type=volume,source=$volumeName,target=/var/lib/postgresql/data"
    $backupMount = "type=bind,source=$directory,target=/backup,readonly"
    Invoke-PgDocker @('run', '-d', '--pull=never', '--name', $prepareName, '--network', 'none',
        '--label', "chatapp.backup.restore=$RestoreName", '--mount', $dataMount, '--mount', $backupMount,
        '--entrypoint', '/bin/sh', $metadata.imageId, '-c', 'sleep 3600') | Out-Null
    # Verify the exported base BEFORE changing recovery configuration or starting PostgreSQL.
    Invoke-PgDocker @('exec', $prepareName, 'sh', '-ec',
        'cp -a /backup/base/. /var/lib/postgresql/data/; pg_verifybackup /var/lib/postgresql/data; cat "/backup/$1" >> /var/lib/postgresql/data/postgresql.auto.conf; rm -f /var/lib/postgresql/data/standby.signal; touch /var/lib/postgresql/data/recovery.signal; chown -R postgres:postgres /var/lib/postgresql/data; chmod 700 /var/lib/postgresql/data',
        '--', $settingsName) | Out-Null
    Invoke-PgDocker @('rm', '--force', $prepareName) | Out-Null
    Invoke-PgDocker @('run', '-d', '--pull=never', '--name', $RestoreName, '--network', 'none',
        '--label', "chatapp.backup.restore=$RestoreName", '--mount', $dataMount, '--mount', $backupMount,
        $metadata.imageId, 'postgres', '-c', 'archive_mode=off', '-c', 'listen_addresses=', '-c', 'hot_standby=on') | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ((Invoke-PgDocker @('inspect', '--format', '{{.State.Running}}', $RestoreName)) -ne 'true') {
            $details = Invoke-PgDocker @('logs', '--tail', '15', $RestoreName)
            throw "Recovery stopped before reaching target: $details"
        }
        try {
            $state = Invoke-PgSql -Container $RestoreName -DbUser $metadata.dbUser -Sql 'SELECT pg_is_in_recovery() AND pg_is_wal_replay_paused();'
        } catch { $state = '' }
        if ($state -eq 't') {
            $systemId = Invoke-PgSql -Container $RestoreName -DbUser $metadata.dbUser -Sql 'SELECT system_identifier FROM pg_control_system();'
            if ($systemId -ne $metadata.systemIdentifier) { throw 'Restored PostgreSQL system ID mismatch.' }
            $exactTarget = Invoke-PgSql -Container $RestoreName -DbUser $metadata.dbUser -Sql "SELECT pg_last_wal_replay_lsn() = '$($point.lsn)'::pg_lsn;"
            if ($exactTarget -ne 't') { throw 'Recovery paused at a different WAL position. Refusing a misleading success.' }
            Write-Host "PASS: $RestoreName paused at $TargetName; read-only, no ports, no application network." -ForegroundColor Green
            return [pscustomobject]@{ Container=$RestoreName; Volume=$volumeName; DbUser=$metadata.dbUser; TargetName=$TargetName }
        }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Recovery did not reach the requested target before timeout.'
} catch {
    if ($owned) { Remove-PgRestore $RestoreName }
    throw
}
