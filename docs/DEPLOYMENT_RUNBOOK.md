# HappyMix production deployment runbook

Prepared 2026-11-10. Deployment tooling is implemented; production has not been launched or verified.

## Current setup

- Oracle Ubuntu 26.04 ARM64 VM; Docker/Compose installed.
- Tailscale SSH connectivity verified to the dedicated deployment account.
- `/opt/happymix/bot.env` exists, belongs to the deployment user and has mode 600.
- User reports the Tailscale OAuth and Oracle SSH repository secrets have been saved. Their values were not read or printed during implementation.
- The workflow uses the existing secret names from ORACLE_CICD_PLAN.md. It does not need Discord/PandaScore tokens in GitHub.
- Runtime credentials remain server-side. A production image is not yet published by this workflow.

## What runs

`.github/workflows/ci-cd.yml` runs tests on pull requests and main pushes. Trusted main runs also build and publish an ARM64 image to `ghcr.io/tta19764/happymixdiscordbot`, tagged by commit and deployed by digest. All third-party actions are pinned to reviewed repository commit IDs.

The deployment job is disabled unless the repository Actions variable `ENABLE_PRODUCTION_DEPLOYMENT` is exactly `true`. Keep it unset initially. This lets the first push validate tests and publish an image without launching the bot.

The server uses the workflow's short-lived, read-only `GITHUB_TOKEN` to pull the image. Docker stores it in a restricted per-release temporary directory, logs out after the pull, and the workflow removes the credential file. The package must grant this repository Actions access; a package created by this workflow normally inherits that linkage. No permanent read-package PAT needs to be copied to the VM for this path.

## First run

1. Review and commit the new production files, then push to main. This triggers test/image jobs only while the deployment variable is absent.
2. In GitHub Actions, confirm the test and image jobs succeed. The workflow installs the SDK from global.json; the Docker build uses the pinned SDK/runtime image versions already in Dockerfile. Do not change the repository or package visibility just to fix a permissions error.
3. Confirm the GHCR package is linked to this repository and permits its Actions workflow to read it. Check the package's Actions access settings if a pull returns 403.
4. Stop any local/other bot using the same token/channel before enabling production. Different SQLite files do not coordinate announcements. Keep local builds/tests running independently if desired, but do not start the publisher.
5. In repository Settings -> Secrets and variables -> Actions -> Variables, create `ENABLE_PRODUCTION_DEPLOYMENT` with value `true`.
6. Open Actions -> HappyMix CI and deployment -> Run workflow, select main and run it. Manual runs still execute tests and build the selected main commit before deploying.
7. The deploy job joins Tailscale as `tag:happymix-ci`, verifies the saved SSH host key, stages the release under `/opt/happymix/releases`, and passes only an immutable image reference, commit and workflow run number to the deployment script.
8. A short planned outage occurs during replacement. The script pulls before stopping the old container, takes an offline snapshot of the full data volume, then starts the new container. The first empty volume also gets a valid empty snapshot.
9. Startup must produce both the Discord connection and successful PandaScore synchronization log signals within 150 seconds, without a container restart. Otherwise the job fails and stops the new container. It does not automatically roll an old image back after migrations may have run.
10. Confirm `/ping` in Discord. Observe real schedule/notification behavior and then verify a container restart retains recorded history.

Successful code deployment alone does not establish Always Free billing eligibility, reboot recovery or off-VM backup recovery. Day 2 remains partial until those checks are completed.

## Inspect the running release

As the deployment user on the VM:

```bash
cd /opt/happymix/current
docker compose -p happymix --env-file image.env -f compose.production.yaml ps
docker compose -p happymix --env-file image.env -f compose.production.yaml logs --tail=100 bot
cat deployed-commit.txt
```

Use `config --quiet` when validating Compose: resolved configuration includes credentials and must not be printed. The service uses raw env-file values so `$` in a token is not interpreted by Compose. Compose needs support for raw env files and `--no-env-resolution`; this was validated with the server's Compose 5.7.0.

## Restart and reboot checks

From the current release directory:

```bash
docker compose -p happymix --env-file image.env -f compose.production.yaml restart bot
```

Verify `/ping`, successful synchronization and absence of repeated recorded announcements. Reboot through the administrator account/Oracle console and verify recovery again. Do not manually stop the service before the reboot check: `unless-stopped` intentionally respects a manually stopped service. Keep the same explicit `happymix-data` volume and channel ID. Do not use `down -v` or prune volumes.

## Backup and recovery

Every deployment snapshots the stopped SQLite volume to `/opt/happymix/backups/history-*.tar.gz`. The snapshot includes WAL/journal files if they remain after shutdown. The snapshot helper runs the already-pulled image with its entrypoint replaced by tar: it never connects a second bot.

No backup is taken while any running container uses the volume. Pre-start failures restore the previous running container; post-start failures leave the new bot stopped because a database migration may have changed its schema. Inspect the failed release's `pre-deploy-backup.txt` and the last successful `/opt/happymix/current` before recovery.

An operator must check schema compatibility before redeploying the previous release. If a restore is needed, stop every publisher, preserve the failed database separately, restore the matching snapshot with the image's application-user ownership, and start only the matching release. Replaying restored old history may resend events. Never restore into a volume still being written.

Download a consistent backup to a separate trusted device before considering disaster recovery covered. Automatic daily/off-VM backup, retention rotation and a restore rehearsal remain future work. These archives and old Docker images consume disk; check `df -h /` and `docker system df` and retain known-good image/backup pairs before removing old artifacts. Do not blindly prune volumes.

## Failed jobs

| Symptom | Check |
| --- | --- |
| Deploy skipped | Variable absent/not exactly `true`, or branch is not main |
| Tailscale authentication failure | OAuth scopes and allowed `tag:happymix-ci`; both TS secrets |
| Tailscale reachable but SSH times out | CI-tag grant to production tag on TCP 22; VM's tag and OS firewall |
| Host-key verification failure | Compare VM host key through trusted administration; update known_hosts only after verification |
| SSH publickey denied | Dedicated private key matches deployed authorized_keys; correct user; newline/CR handling |
| GHCR pull denied | Package linkage/Actions read access; image digest exists |
| Backup refused | Another container uses the same volume; stop/reconcile it before retrying |
| Startup verification failure | Inspect logs on the VM: credentials, Discord channel access, migrations and API responses |
| Stale deployment refused | A newer workflow run already succeeded; do not force an older overlapping run |

Turning the deployment variable off prevents subsequent jobs; it does not stop a running bot or interrupt an already-started deployment. Workflow concurrency and a server lock prevent overlapping deployment scripts; a run-number guard prevents a slower old build replacing a newer release.

## Validation performed

- 66 .NET tests passed in Release using the installed 10.0.402 SDK from the isolated validation directory; root SDK pin unchanged.
- 10 fake-Docker deployment lifecycle scenarios passed under real Linux bash on the VM in isolated temporary directories. No bot was started, stopped or deployed during these tests.
- Production Compose config validated without displaying runtime secrets; shell syntax and workflow validation passed.
- Actions pins resolved from their upstream repositories. Image build, GHCR access, CI OAuth authentication and live deployment still require the first real GitHub run.
