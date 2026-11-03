#!/bin/sh
set -eu
mkdir -p "$PGDATA"
chown postgres:postgres "$PGDATA"
chmod 700 "$PGDATA"
runner=$(command -v gosu || command -v su-exec)
if [ ! -s "$PGDATA/PG_VERSION" ]; then
    "$runner" postgres pg_basebackup -h postgres -U backup_replication -D "$PGDATA" -R -X stream -S chatapp_standby --checkpoint=fast --no-password
fi
exec docker-entrypoint.sh postgres -c archive_mode=off -c hot_standby=on
