#!/bin/sh
set -eu
file=${1:?usage: restore.sh backup.sql.gz.enc}
: "${BACKUP_PASSPHRASE:?}"
echo "这会把备份写回数据库。请先停掉 api 容器。"
openssl enc -d -aes-256-cbc -pbkdf2 -pass env:BACKUP_PASSPHRASE -in "$file" | gzip -dc | docker compose exec -T db psql -U fish -d fish
