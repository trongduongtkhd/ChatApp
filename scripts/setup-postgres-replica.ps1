. "$PSScriptRoot/postgres-backup-common.ps1"
# Secret stays inside the source container environment, never on the command line.
$scriptText = @'
psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres <<'SQL'
\getenv replica_password POSTGRES_PASSWORD
SELECT 'CREATE ROLE backup_replication WITH LOGIN REPLICATION' WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='backup_replication') \gexec
SELECT format('ALTER ROLE backup_replication WITH LOGIN REPLICATION PASSWORD %L', :'replica_password') \gexec
SELECT pg_create_physical_replication_slot('chatapp_standby') WHERE NOT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name='chatapp_standby');
SQL
rule='host replication backup_replication all scram-sha-256'
grep -Fqx "$rule" "$PGDATA/pg_hba.conf" || printf '\n%s\n' "$rule" >> "$PGDATA/pg_hba.conf"
psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -c 'SELECT pg_reload_conf();'
'@
Invoke-PgDocker @('exec','--user','postgres','postgres','sh','-ec',$scriptText.Replace("`r`n","`n")) | Out-Null
Invoke-PgDocker @('compose','--profile','replication','up','-d','postgres-standby') | Out-Null
Write-Host 'PostgreSQL standby started; source volume unchanged.'
