#!/bin/sh
set -eu
: "${POSTGRES_PASSWORD:?}"
: "${BACKUP_PASSPHRASE:?}"
dir="${BACKUP_DIR:-/var/backups/bigbluefish}"
mkdir -p "$dir"
stamp=$(date -u +%Y%m%dT%H%M%SZ)
file="$dir/fish-$stamp.sql.gz.enc"
docker compose exec -T db pg_dump -U fish fish | gzip | openssl enc -aes-256-cbc -pbkdf2 -pass env:BACKUP_PASSPHRASE -out "$file"
echo "encrypted backup: $file"
if [ -n "${OBS_BACKUP_URI:-}" ] && command -v obsutil >/dev/null 2>&1; then
  obsutil cp "$file" "$OBS_BACKUP_URI"
fi
