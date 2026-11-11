#!/usr/bin/env bash
# Deployment lifecycle tests using a fake Docker CLI: no Docker socket, network or tokens.
set -Eeuo pipefail
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
workspace=$(mktemp -d /tmp/happymix-deploy-tests.XXXXXX)
trap '[[ $workspace == /tmp/happymix-deploy-tests.* ]] && rm -rf -- "$workspace"' EXIT
mkdir "$workspace/bin"
cat >"$workspace/bin/docker" <<'DOCKER'
#!/usr/bin/env bash
set -eu
printf '%s\n' "$*" >>"$TEST_STATE/calls"
case "$1" in
  pull) [[ $SCENARIO != pull-fail ]];;
  image|version) echo arm64;;
  volume) echo happymix-data;;
  ps)
    if [[ $* == *'-aq'* ]]; then echo old;
    elif [[ $SCENARIO == other-container ]]; then echo unrelated;
    elif [[ -e $TEST_STATE/old.running ]]; then echo old; fi;;
  inspect)
    if [[ $* == *RestartCount* ]]; then echo 0;
    elif [[ ${*: -1} == old ]]; then test ! -e "$TEST_STATE/old.running" && echo false || echo true;
    else test ! -e "$TEST_STATE/new.running" && echo false || echo true; fi;;
  stop) rm -f "$TEST_STATE/old.running";;
  start) touch "$TEST_STATE/old.running";;
  run)
    [[ $SCENARIO != backup-fail ]] || exit 1
    printf 'test snapshot\n' >"$HAPPYMIX_ROOT/backups/${@: -3:1}";;
  logs)
    if [[ $SCENARIO != unhealthy ]]; then
      printf 'Connected to Discord as Test\nPandaScore match synchronization completed\n'
    fi;;
  compose)
    if [[ $* == *'config --quiet'* ]]; then [[ $SCENARIO != config-fail ]];
    elif [[ $* == *'up -d'* ]]; then [[ $SCENARIO != start-fail ]] || exit 1; touch "$TEST_STATE/new.running";
    elif [[ $* == *'stop bot'* ]]; then rm -f "$TEST_STATE/new.running";
    elif [[ $* == *'ps -q bot'* ]]; then echo new;
    else exit 3; fi;;
  *) echo "Unexpected fake Docker command: $*" >&2; exit 3;;
esac
DOCKER
printf '#!/usr/bin/env bash\nexit 0\n' >"$workspace/bin/sleep"
chmod +x "$workspace/bin/"*
export PATH="$workspace/bin:$PATH"
image="ghcr.io/tta19764/happymixdiscordbot@sha256:$(printf 'a%.0s' {1..64})"
commit=$(printf 'b%.0s' {1..40})

for scenario in success pull-fail backup-fail config-fail start-fail unhealthy other-container bad-mode; do
  export SCENARIO=$scenario
  export HAPPYMIX_ROOT="$workspace/$scenario"
  export TEST_STATE="$HAPPYMIX_ROOT/state"
  release="$HAPPYMIX_ROOT/releases/test"
  mkdir -p "$TEST_STATE" "$release"
  cp "$repo/deploy/deploy.sh" "$repo/deploy/backup.sh" "$repo/compose.production.yaml" "$release/"
  printf 'fake-runtime-values\n' >"$HAPPYMIX_ROOT/bot.env"
  chmod 600 "$HAPPYMIX_ROOT/bot.env"
  touch "$TEST_STATE/old.running"
  if [[ $scenario == bad-mode ]]; then chmod 644 "$HAPPYMIX_ROOT/bot.env"; fi
  if bash "$release/deploy.sh" "$image" "$commit" 1 >"$HAPPYMIX_ROOT/output" 2>&1; then
    [[ $scenario == success ]] || { cat "$HAPPYMIX_ROOT/output"; exit 1; }
    test -L "$HAPPYMIX_ROOT/current"
    test "$(cat "$release/deployed-image.txt")" = "$image"
    test -s "$(cat "$release/pre-deploy-backup.txt")"
    test -f "$TEST_STATE/new.running"
    test ! -e "$TEST_STATE/old.running"
  else
    [[ $scenario != success ]] || { cat "$HAPPYMIX_ROOT/output"; exit 1; }
    test ! -L "$HAPPYMIX_ROOT/current"
    case "$scenario" in
      start-fail|unhealthy) test ! -e "$TEST_STATE/new.running"; test ! -e "$TEST_STATE/old.running";;
      *) test -e "$TEST_STATE/old.running";;
    esac
  fi
  echo "PASS: $scenario"
done

# Invalid immutable references must be rejected before any Docker action.
if bash "$release/deploy.sh" 'ghcr.io/example/bot:latest' "$commit" 1 >/dev/null 2>&1; then
  echo 'Mutable tag was unexpectedly accepted' >&2; exit 1
fi
echo 'PASS: mutable image rejected'

export SCENARIO=stale
export HAPPYMIX_ROOT="$workspace/success"
export TEST_STATE="$HAPPYMIX_ROOT/state"
printf '99\n' >"$HAPPYMIX_ROOT/current/deployed-run.txt"
if bash "$HAPPYMIX_ROOT/current/deploy.sh" "$image" "$commit" 1 >/dev/null 2>&1; then
  echo 'An older run unexpectedly replaced a newer deployment' >&2; exit 1
fi
test -f "$TEST_STATE/new.running"
echo 'PASS: stale release rejected'
