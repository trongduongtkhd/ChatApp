#!/bin/sh
# PostgreSQL calls this as its OS user. Return success only after a durable copy.
set -eu
umask 077
source_file=$1
wal_name=$2
if ! printf '%s\n' "$wal_name" | grep -Eq '^([0-9A-F]{24}(\.[0-9A-F]{8}\.backup)?|[0-9A-F]{8}\.history)$'; then
    echo 'Invalid WAL archive filename' >&2
    exit 1
fi
# A new PostgreSQL cluster can reuse WAL filenames, so namespace by system ID.
system_id=$(LC_ALL=C pg_controldata "$PGDATA" | sed -n 's/^Database system identifier: *//p')
case "$system_id" in ''|*[!0-9]*) echo 'Cannot read PostgreSQL system identifier' >&2; exit 1;; esac
archive_dir="/var/lib/postgresql/wal-archive/$system_id"
mkdir -p "$archive_dir"
destination="$archive_dir/$wal_name"
if [ -f "$destination" ]; then
    cmp -s "$source_file" "$destination"
    sync "$destination"
    sync "$archive_dir"
    exit 0
fi
temporary="$destination.$$.tmp"
trap 'rm -f "$temporary"' EXIT HUP INT TERM
cp "$source_file" "$temporary"
sync "$temporary"
# Atomic publication without overwriting an existing archived segment.
if ! ln "$temporary" "$destination" 2>/dev/null; then
    cmp -s "$temporary" "$destination"
fi
sync "$archive_dir"
