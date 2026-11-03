param(
    [string]$SourceContainer = 'postgres',
    [switch]$KeepRestore
)
. "$PSScriptRoot/postgres-backup-common.ps1"
$source = Get-PgSource $SourceContainer
$suffix = [Guid]::NewGuid().ToString('N').Substring(0,12)
$fixtureDatabase = "backup_demo_$suffix"
$restoreName = "chatapp-pg-restore-$suffix"
$targetName = "before_delete_$suffix"
$createdFixture = $false
$passed = 0
$backup = $null
$restore = $null
$writeJob = $null
$report = $null

function Assert-BackupCheck {
    param([string]$Name, [bool]$Condition)
    if (!$Condition) { throw "FAIL: $Name" }
    $script:passed++
    Write-Host "PASS $script:passed - $Name" -ForegroundColor Green
}

function Get-BusinessFingerprint {
    param([object]$Source)
    $fingerprints = [ordered]@{}
    foreach ($database in @('identity_db','group_db','chat_db','notification_db')) {
        # Hash server-side, so account/password hashes and message contents never
        # leave PostgreSQL as tool output. Include every public table and row.
        $tables = Invoke-PgSql -Container $Source.Container -DbUser $Source.DbUser -Database $database -Sql @'
SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename;
'@
        $rows = foreach ($table in @($tables -split "`n" | Where-Object { $_ })) {
            $identifier = '"' + $table.Replace('"','""') + '"'
            Invoke-PgSql -Container $Source.Container -DbUser $Source.DbUser -Database $database -Sql @"
SELECT '$($table.Replace("'","''")):' || count(*) || ':' || COALESCE(md5(string_agg(to_jsonb(t)::text, E'\n' ORDER BY to_jsonb(t)::text)), 'empty') FROM public.$identifier t;
"@
        }
        $fingerprints[$database] = ($rows -join '|')
    }
    return ($fingerprints | ConvertTo-Json -Compress)
}

Write-Host 'PostgreSQL-only backup/PITR demo. All DELETE statements target a new backup_demo_* database.' -ForegroundColor Cyan
$beforeFingerprint = Get-BusinessFingerprint $source
try {
    Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql "CREATE DATABASE $fixtureDatabase;" | Out-Null
    $createdFixture = $true
    Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Database $fixtureDatabase -Sql @'
CREATE TABLE demo_messages (id integer PRIMARY KEY, content text NOT NULL);
INSERT INTO demo_messages VALUES (1, 'A - before base backup');
CREATE TABLE write_probe (id bigserial PRIMARY KEY, written_at timestamptz NOT NULL DEFAULT clock_timestamp());
'@ | Out-Null
    # Continuous committed writes during a rate-limited real pg_basebackup.
    $writeJob = Start-Job -ArgumentList $PSScriptRoot,$source.Container,$source.DbUser,$fixtureDatabase -ScriptBlock {
        param($scriptsDirectory,$container,$dbUser,$database)
        . "$scriptsDirectory/postgres-backup-common.ps1"
        while ($true) {
            Invoke-PgSql -Container $container -DbUser $dbUser -Database $database -Sql 'INSERT INTO write_probe (written_at) SELECT clock_timestamp() WHERE EXISTS (SELECT 1 FROM pg_stat_progress_basebackup);' | Out-Null
            Start-Sleep -Milliseconds 250
        }
    }
    $baseStart = Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql 'SELECT clock_timestamp();'
    $backup = & "$PSScriptRoot/backup-postgres.ps1" -SourceContainer $source.Container -MaxRateKb 4096
    $baseEnd = Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql 'SELECT clock_timestamp();'
    Stop-Job $writeJob
    $jobErrors = @()
    Receive-Job $writeJob -ErrorVariable +jobErrors | Out-Null
    Assert-BackupCheck 'Concurrent SQL writer had no errors' ($jobErrors.Count -eq 0)
    $writes = [int](Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Database $fixtureDatabase -Sql "SELECT count(*) FROM write_probe WHERE written_at > '$baseStart'::timestamptz AND written_at < '$baseEnd'::timestamptz;")
    Assert-BackupCheck "SQL writes succeeded during base backup ($writes committed rows)" ($writes -ge 2)
    Assert-BackupCheck 'Primary did not restart during backup' ((Invoke-PgDocker @('inspect','--format','{{.State.StartedAt}}',$source.Container)) -eq $source.StartedAt)
    Assert-BackupCheck 'Complete base backup metadata exists' ((Get-PgBackupMetadata $backup.BackupDirectory).status -eq 'complete')

    Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Database $fixtureDatabase -Sql "INSERT INTO demo_messages VALUES (2, 'B - committed after base backup');" | Out-Null
    Assert-BackupCheck 'Source now contains A and B' ((Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Database $fixtureDatabase -Sql 'SELECT string_agg(id::text, chr(44) ORDER BY id) FROM demo_messages;') -eq '1,2')
    $point = & "$PSScriptRoot/new-postgres-restore-point.ps1" -BackupDirectory $backup.BackupDirectory -Name $targetName -SourceContainer $source.Container
    Assert-BackupCheck 'Named recovery point and WAL files sealed' (@($point.walFiles).Count -gt 0)
    Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Database $fixtureDatabase -Sql 'DELETE FROM demo_messages WHERE id=2;' | Out-Null
    Assert-BackupCheck 'Simulated accidental deletion: source only has A' ((Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Database $fixtureDatabase -Sql 'SELECT string_agg(id::text, chr(44) ORDER BY id) FROM demo_messages;') -eq '1')

    $restore = & "$PSScriptRoot/restore-postgres.ps1" -BackupDirectory $backup.BackupDirectory -TargetName $targetName -RestoreName $restoreName
    $recoveredIds = Invoke-PgSql -Container $restore.Container -DbUser $restore.DbUser -Database $fixtureDatabase -Sql 'SELECT string_agg(id::text, chr(44) ORDER BY id) FROM demo_messages;'
    Assert-BackupCheck 'PITR recovered A and B, before deletion' ($recoveredIds -eq '1,2')
    $b = Invoke-PgSql -Container $restore.Container -DbUser $restore.DbUser -Database $fixtureDatabase -Sql 'SELECT content FROM demo_messages WHERE id=2;'
    Assert-BackupCheck 'B was recovered from WAL beyond the base backup' ($b -eq 'B - committed after base backup')
    Assert-BackupCheck 'Recovery paused at target, stays read-only' ((Invoke-PgSql -Container $restore.Container -DbUser $restore.DbUser -Sql 'SHOW transaction_read_only;') -eq 'on')
    Assert-BackupCheck 'Restore has network mode none' ((Invoke-PgDocker @('inspect','--format','{{.HostConfig.NetworkMode}}',$restore.Container)) -eq 'none')
    Assert-BackupCheck 'Restore publishes no host ports' ((Invoke-PgDocker @('inspect','--format','{{json .HostConfig.PortBindings}}',$restore.Container)) -in @('null','{}'))

    $rejected = $false
    try { & "$PSScriptRoot/restore-postgres.ps1" -BackupDirectory $backup.BackupDirectory -TargetName $targetName -RestoreName $restoreName | Out-Null } catch { $rejected = $_.Exception.Message -match 'already exists' }
    Assert-BackupCheck 'Existing restore container/volume cannot be overwritten' $rejected
    $rejected = $false
    try { & "$PSScriptRoot/restore-postgres.ps1" -BackupDirectory $backup.BackupDirectory -TargetName 'unknown_point' -RestoreName "chatapp-pg-restore-bad-$suffix" | Out-Null } catch { $rejected = $_.Exception.Message -match 'sealed recovery point' }
    Assert-BackupCheck 'Unknown recovery point is refused' $rejected

    # Test guards on this suite's own bundle, then restore the original bytes.
    $manifestPath = Join-Path $backup.BackupDirectory 'base/backup_manifest'
    $original = [IO.File]::ReadAllBytes($manifestPath)
    try {
        [IO.File]::WriteAllText($manifestPath, 'corrupted test manifest')
        $rejected = $false
        try { & "$PSScriptRoot/restore-postgres.ps1" -BackupDirectory $backup.BackupDirectory -TargetName $targetName -RestoreName "chatapp-pg-restore-bad-$suffix" | Out-Null } catch { $rejected = $_.Exception.Message -match 'manifest hash mismatch' }
        Assert-BackupCheck 'Corrupted backup manifest is refused' $rejected
    } finally { [IO.File]::WriteAllBytes($manifestPath, $original) }
    $versionPath = Join-Path $backup.BackupDirectory 'base/PG_VERSION'
    $originalVersion = [IO.File]::ReadAllBytes($versionPath)
    try {
        [IO.File]::WriteAllText($versionPath, "16`n")
        $rejected = $false
        try { & "$PSScriptRoot/restore-postgres.ps1" -BackupDirectory $backup.BackupDirectory -TargetName $targetName -RestoreName "chatapp-pg-restore-bad-$suffix" | Out-Null } catch { $rejected = $_.Exception.Message -match 'checksum mismatch|verification failed' }
        Assert-BackupCheck 'pg_verifybackup detects a corrupted base data file' $rejected
        Assert-BackupCheck 'Failed recovery removed its own temporary container and volume' (!(Test-PgContainerExists "chatapp-pg-restore-bad-$suffix") -and !(Test-PgVolumeExists "chatapp-pg-restore-bad-$suffix-data"))
    } finally { [IO.File]::WriteAllBytes($versionPath, $originalVersion) }
    $walPath = Join-Path (Join-Path $backup.BackupDirectory 'wal') $point.walFile
    $hiddenWalPath = "$walPath.hidden-for-test"
    try {
        Move-Item -LiteralPath $walPath -Destination $hiddenWalPath
        $rejected = $false
        try { & "$PSScriptRoot/restore-postgres.ps1" -BackupDirectory $backup.BackupDirectory -TargetName $targetName -RestoreName "chatapp-pg-restore-bad-$suffix" | Out-Null } catch { $rejected = $_.Exception.Message -match 'Missing archived WAL' }
        Assert-BackupCheck 'Missing WAL is refused instead of reporting recovery success' $rejected
    } finally { if (Test-Path -LiteralPath $hiddenWalPath) { Move-Item -LiteralPath $hiddenWalPath -Destination $walPath } }
    $originalWal = [IO.File]::ReadAllBytes($walPath)
    try {
        [IO.File]::WriteAllText($walPath, 'corrupted WAL for this fixture only')
        $rejected = $false
        try { & "$PSScriptRoot/restore-postgres.ps1" -BackupDirectory $backup.BackupDirectory -TargetName $targetName -RestoreName "chatapp-pg-restore-bad-$suffix" | Out-Null } catch { $rejected = $_.Exception.Message -match 'WAL checksum mismatch' }
        Assert-BackupCheck 'Corrupted WAL is refused before starting recovery' $rejected
    } finally { [IO.File]::WriteAllBytes($walPath, $originalWal) }
    $report = [ordered]@{ passed=$passed; backupDirectory=$backup.BackupDirectory; fixtureDatabase=$fixtureDatabase;
        restoreContainer=$restoreName; targetName=$targetName; onlineWrites=$writes; recoveredIds=$recoveredIds;
        realDataUnchanged=$true; completedAtUtc=[DateTime]::UtcNow.ToString('o'); keepRestore=[bool]$KeepRestore }
} finally {
    if ($null -ne $writeJob) { Stop-Job $writeJob -ErrorAction SilentlyContinue; Remove-Job $writeJob -Force -ErrorAction SilentlyContinue }
    if ($createdFixture) {
        if ($fixtureDatabase -notmatch '^backup_demo_[a-f0-9]{12}$') { throw 'Unsafe fixture database name.' }
        Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql "DROP DATABASE $fixtureDatabase WITH (FORCE);" | Out-Null
    }
    if (!$KeepRestore -or $null -eq $restore) { Remove-PgRestore $restoreName }
}
Assert-BackupCheck 'Demo database was removed from the source after the suite' ((Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql "SELECT count(*) FROM pg_database WHERE datname='$fixtureDatabase';") -eq '0')
Assert-BackupCheck 'Real application data fingerprints unchanged after cleanup' ((Get-BusinessFingerprint $source) -eq $beforeFingerprint)
if (!$KeepRestore) {
    Assert-BackupCheck 'Restore container and volume were cleaned' (!(Test-PgContainerExists $restoreName) -and !(Test-PgVolumeExists "$restoreName-data"))
}
$report.passed = $passed
$report['sourceFixtureRemoved'] = $true
Write-PgBackupJson -Path (Join-Path $backup.BackupDirectory 'test-report.json') -Value $report
Write-Host "`nPASS: $passed checks. Source A; isolated restored copy A+B. Real data unchanged." -ForegroundColor Green
Write-Host "Backup bundle: $($backup.BackupDirectory)"
if ($KeepRestore) {
    Write-Host "Inspect: docker exec --user postgres $restoreName psql -U $($source.DbUser) -d $fixtureDatabase -c 'TABLE demo_messages;'"
    Write-Host "Cleanup: . ./scripts/postgres-backup-common.ps1; Remove-PgRestore '$restoreName'"
}
