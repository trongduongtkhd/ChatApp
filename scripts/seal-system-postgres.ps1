param(
    [Parameter(Mandatory=$true)][string]$BackupDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$')][string]$TargetName,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9A-F]+/[0-9A-F]+$')][string]$TargetLsn
)
. "$PSScriptRoot/postgres-backup-common.ps1"
$directory = Get-PgBackupDirectory $BackupDirectory
$metadata = Get-PgBackupMetadata $directory
$source = Get-PgSource
if ($source.SystemId -ne $metadata.systemIdentifier -or $source.Timeline -ne $metadata.timeline) { throw 'PostgreSQL source generation changed.' }
if (@($metadata.recoveryPoints).Count -ne 0) { throw 'System base already has a sealed recovery point.' }
# The coordinator ALREADY wrote this marker under the cross-service barrier.
# Never call pg_create_restore_point here: doing so would capture a later state.
$walFile = Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql "SELECT pg_walfile_name('$TargetLsn'::pg_lsn);"
Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql 'SELECT pg_switch_wal();' | Out-Null
Wait-PgArchivedWal -Source $source -WalFile $walFile
$walDirectory = Save-PgArchivedWal -Source $source -BackupDirectory $directory -ThroughWal $walFile
$files = @(Get-ChildItem -LiteralPath $walDirectory -File | Where-Object { $_.Name -match '^([0-9A-F]{24}(\.[0-9A-F]{8}\.backup)?|[0-9A-F]{8}\.history)$' } |
    Sort-Object Name | ForEach-Object { @{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
$metadata.recoveryPoints = @([pscustomobject]@{ name=$TargetName; lsn=$TargetLsn; recordedAtUtc=[DateTime]::UtcNow.ToString('o'); walFile=$walFile; walFiles=$files })
Write-PgBackupJson -Path (Join-Path $directory 'metadata.json') -Value $metadata
Write-Host "PostgreSQL checkpoint sealed: $TargetName ($TargetLsn)."
