#!/bin/sh
# Scheduled PostgreSQL backups for the "backup" profile of docker-compose.prod.yml.
# Writes a custom-format dump (restore with pg_restore) every BACKUP_INTERVAL_SECONDS into /backups and deletes
# dumps older than BACKUP_RETAIN_DAYS. Connection settings come from PGHOST/PGUSER/PGPASSWORD/PGDATABASE.
set -eu
: "${BACKUP_INTERVAL_SECONDS:=86400}"
: "${BACKUP_RETAIN_DAYS:=14}"
umask 077

while :; do
  stamp=$(date -u +%Y%m%dT%H%M%SZ)
  file="/backups/${PGDATABASE}-${stamp}.dump"
  if pg_dump --format=custom --compress=9 --no-owner --file="${file}.partial"; then
    mv "${file}.partial" "${file}"
    echo "$(date -u +%FT%TZ) backup ok: ${file} ($(du -h "${file}" | cut -f1))"
  else
    rm -f "${file}.partial"
    echo "$(date -u +%FT%TZ) backup FAILED" >&2
  fi
  find /backups -name "${PGDATABASE}-*.dump" -type f -mtime "+${BACKUP_RETAIN_DAYS}" -print -delete
  sleep "${BACKUP_INTERVAL_SECONDS}"
done
