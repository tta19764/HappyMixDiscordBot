#!/usr/bin/env bash
# Offline snapshot of the entire SQLite volume, including any uncheckpointed sidecars.
# The caller must stop every bot using this volume before calling this script.
set -Eeuo pipefail
umask 077

root=${HAPPYMIX_ROOT:-/opt/happymix}
image=${1:?Usage: backup.sh IMAGE_DIGEST}
[[ $image =~ ^ghcr\.io/[a-z0-9._/-]+@sha256:[a-f0-9]{64}$ ]] || { echo 'Invalid image digest' >&2; exit 2; }
[[ $root == /* && $root != / ]] || { echo 'Expected an absolute deployment directory' >&2; exit 2; }

# The publisher mounts this volume read/write. Refuse even a manually launched publisher.
if [[ -n $(docker ps -q --filter volume=happymix-data) ]]; then
  echo 'Stop containers using happymix-data before an offline backup.' >&2
  exit 1
fi

mkdir -p "$root/backups"
chmod 700 "$root/backups"
docker volume create happymix-data >/dev/null
name="history-$(date -u +%Y%m%dT%H%M%S)-$$.tar.gz"
docker run --rm --network none --user 0 --entrypoint /bin/sh \
  --mount type=volume,source=happymix-data,target=/data,readonly \
  --mount "type=bind,source=$root/backups,target=/backup" \
  "$image" -c 'umask 077; tar -C /data -czf "/backup/$1" .; chown "$2:$3" "/backup/$1"' \
  sh "$name" "$(id -u)" "$(id -g)"
test -s "$root/backups/$name"
printf '%s\n' "$root/backups/$name"
