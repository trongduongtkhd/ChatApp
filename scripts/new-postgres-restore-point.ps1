param(
    [Parameter(Mandatory=$true)][string]$BackupDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$')][string]$Name,
    [string]$SourceContainer = 'postgres'
)
. "$PSScriptRoot/postgres-backup-common.ps1"
$directory = Get-PgBackupDirectory $BackupDirectory
$metadata = Get-PgBackupMetadata $directory
$source = Get-PgSource $SourceContainer
if ($source.SystemId -ne $metadata.systemIdentifier) { throw 'Backup and source belong to different PostgreSQL clusters.' }
if ($source.Timeline -ne $metadata.timeline) { throw 'Source timeline changed. Create a fresh base backup for this step.' }
if (@($metadata.recoveryPoints | Where-Object { $_.name -eq $Name }).Count -gt 0) { throw 'This restore point name is already recorded.' }
# Name validation above makes this SQL literal safe. A unique name is recommended.
$lsn = Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql "SELECT pg_create_restore_point('$Name');"
$walFile = Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql "SELECT pg_walfile_name('$lsn'::pg_lsn);"
Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql 'SELECT pg_switch_wal();' | Out-Null
Wait-PgArchivedWal -Source $source -WalFile $walFile
$walDirectory = Save-PgArchivedWal -Source $source -BackupDirectory $directory -ThroughWal $walFile
# Record checksums of every segment included in this recovery point's bundle.
$files = @(Get-ChildItem -LiteralPath $walDirectory -File | Where-Object { $_.Name -match '^([0-9A-F]{24}(\.[0-9A-F]{8}\.backup)?|[0-9A-F]{8}\.history)$' } |
    Sort-Object Name | ForEach-Object { @{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
$point = [pscustomobject]@{ name=$Name; lsn=$lsn; recordedAtUtc=[DateTime]::UtcNow.ToString('o'); walFile=$walFile; walFiles=$files }
$metadata.recoveryPoints = @($metadata.recoveryPoints) + @($point)
Write-PgBackupJson -Path (Join-Path $directory 'metadata.json') -Value $metadata
Write-Host "PASS: restore point $Name at LSN $lsn; required WAL copied into backup bundle." -ForegroundColor Green
return $point
