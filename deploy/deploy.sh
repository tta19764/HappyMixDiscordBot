#!/usr/bin/env bash
# Deploy exactly one tested image. Runtime secrets remain on the VM.
set -Eeuo pipefail
umask 077

image=${1:?Usage: deploy.sh IMAGE_DIGEST COMMIT_SHA}
commit=${2:?Usage: deploy.sh IMAGE_DIGEST COMMIT_SHA RUN_NUMBER}
revision=${3:?Usage: deploy.sh IMAGE_DIGEST COMMIT_SHA RUN_NUMBER}
root=${HAPPYMIX_ROOT:-/opt/happymix}
release=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
[[ $image =~ ^ghcr\.io/[a-z0-9._/-]+@sha256:[a-f0-9]{64}$ ]] || { echo 'Invalid image digest' >&2; exit 2; }
[[ $commit =~ ^[a-f0-9]{40}$ ]] || { echo 'Invalid commit SHA' >&2; exit 2; }
[[ $revision =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid workflow run number' >&2; exit 2; }
[[ $root == /* && $root != / ]] || { echo 'Expected an absolute deployment directory' >&2; exit 2; }
test -s "$root/bot.env"
test -f "$release/compose.production.yaml"
[[ $(stat -c %a "$root/bot.env") == 600 ]] || { echo 'bot.env must have mode 600' >&2; exit 1; }
command -v flock >/dev/null
exec 9>"$root/deploy.lock"
flock -n 9 || { echo 'Another deployment is in progress' >&2; exit 1; }
if [[ -f $root/current/deployed-run.txt ]]; then
  previous_revision=$(cat "$root/current/deployed-run.txt")
  [[ $previous_revision =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid previous deployment metadata' >&2; exit 1; }
  (( revision >= previous_revision )) || { echo 'A newer workflow has already deployed; refusing a stale release.' >&2; exit 1; }
fi

printf 'HAPPYMIX_IMAGE=%s\n' "$image" >"$release/image.env"
compose=(docker compose -p happymix --env-file "$release/image.env" -f "$release/compose.production.yaml")
# --quiet is intentional: printing the resolved configuration would disclose runtime secrets.
"${compose[@]}" config --quiet
docker pull "$image"
# Subsequent operations use this already-local digest; discard temporary registry credentials early.
if [[ -n ${DOCKER_CONFIG:-} ]]; then docker logout ghcr.io >/dev/null; fi
[[ $(docker image inspect --format '{{.Architecture}}' "$image") == "$(docker version --format '{{.Server.Arch}}')" ]] || {
  echo 'Image and VM architecture do not match' >&2; exit 1;
}

old=$(docker ps -aq --filter label=com.docker.compose.project=happymix --filter label=com.docker.compose.service=bot)
[[ $(printf '%s\n' "$old" | sed '/^$/d' | wc -l) -le 1 ]] || { echo 'Multiple existing bot containers; review manually' >&2; exit 1; }
old_running=false
if [[ -n $old && $(docker inspect --format '{{.State.Running}}' "$old") == true ]]; then
  old_running=true
fi
started=false
finished=false
backup='not-created'
on_exit() {
  status=$?
  if [[ $status -ne 0 && $finished == false ]]; then
    if [[ $started == true ]]; then
      # Migrations may already have run. Do not blindly roll the old image back.
      "${compose[@]}" stop bot >/dev/null 2>&1 || true
    elif [[ $old_running == true ]]; then
      docker start "$old" >/dev/null 2>&1 || true
    fi
    printf 'Deployment failed. Backup: %s. See previous successful release at %s/current.\n' "$backup" "$root" >&2
  fi
}
trap on_exit EXIT
if [[ -n $old ]]; then docker stop --time 45 "$old" >/dev/null; fi
# Backup refuses to proceed if any other container still mounts the data volume.
backup=$(HAPPYMIX_ROOT="$root" bash "$release/backup.sh" "$image")
printf '%s\n' "$backup" >"$release/pre-deploy-backup.txt"
started=true
"${compose[@]}" up -d --no-build --force-recreate bot
container=$("${compose[@]}" ps -q bot)
test -n "$container"

# A running process alone is not proof of a working Discord connection/provider feed.
healthy=false
for ((attempt=0; attempt<30; attempt++)); do
  if [[ $(docker inspect --format '{{.State.Running}}' "$container") != true ||
        $(docker inspect --format '{{.RestartCount}}' "$container") != 0 ]]; then break; fi
  logs=$(docker logs "$container" 2>&1)
  if grep -q 'Connected to Discord as' <<<"$logs" &&
     grep -q 'PandaScore match synchronization completed' <<<"$logs"; then
    healthy=true
    break
  fi
  sleep 5
done
[[ $healthy == true ]] || { echo 'Startup verification failed; inspect container logs on the VM.' >&2; exit 1; }

printf '%s\n' "$image" >"$release/deployed-image.txt"
printf '%s\n' "$commit" >"$release/deployed-commit.txt"
printf '%s\n' "$revision" >"$release/deployed-run.txt"
if [[ -L $root/current ]]; then readlink "$root/current" >"$release/previous-release.txt"; fi
ln -sfn "$release" "$root/current"
finished=true
printf 'Deployed commit %s successfully. Database backup: %s\n' "$commit" "$backup"
