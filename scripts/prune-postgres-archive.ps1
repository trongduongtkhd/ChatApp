param([ValidateRange(24,720)][int]$RetentionHours = 168)
. "$PSScriptRoot/postgres-backup-common.ps1"
$source = Get-PgSource
if ((Invoke-PgSql -Container $source.Container -DbUser $source.DbUser -Sql 'SELECT count(*) FROM pg_stat_progress_basebackup;') -ne '0') {
    Write-Host 'Archive pruning skipped while a base backup is active.'; return
}
$archivePath = "/var/lib/postgresql/wal-archive/$($source.SystemId)"
# This is a fixed container archive namespace, not a Windows path or pg_wal.
$shell = @'
case "$1" in /var/lib/postgresql/wal-archive/[0-9]*) ;; *) exit 1;; esac
find "$1" -maxdepth 1 -type f -name '????????????????????????' -mmin "+$2" -print -delete
'@
$removed = Invoke-PgDocker @('exec','--user','postgres',$source.Container,'sh','-ec',$shell.Replace("`r`n","`n"),'--',$archivePath,([string]($RetentionHours*60)))
$count = @($removed -split "`n" | Where-Object {$_}).Count
Write-Host "Archive retention: removed $count old completed segments; kept base bundles and pg_wal."
