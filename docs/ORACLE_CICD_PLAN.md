# HappyMix: Oracle Always Free and GitHub CI/CD setup plan

Prepared: 2026-11-10. This is a setup plan, not a completed deployment.
Repository: https://github.com/tta19764/HappyMixDiscordBot

## Target setup

Pull request -> GitHub Actions builds and tests.
Merge to main -> test -> build ARM64 image -> publish to GHCR -> SSH over Tailscale -> update one Docker Compose service on Oracle.

SQLite stays on the VM in the existing persistent volume. Bot tokens stay in a protected file on the VM. The deployment records an immutable image digest, not just a mutable latest tag. No domain, HTTP server, inbound application port, paid database, or self-hosted GitHub runner is required.

Recommended first VM: Ubuntu 24.04 ARM64 on VM.Standard.A1.Flex, 1 OCPU and 2 GB RAM if the console permits this Always Free configuration; increase memory only inside the verified free allocation if measurement warrants it. Use an Always Free eligible boot volume, typically 50 GB. Verify the actual tenancy/region limits and estimate before creation.

Oracle currently documents Always Free A1 capacity equivalent to 2 OCPUs and 12 GB RAM in total for Always Free tenancies, plus shared storage allowances. Older 4 OCPU/24 GB tutorials do not establish your current entitlement. Capacity availability and idle-instance reclamation remain limitations.

## 1. Confirm accounts and cost boundaries — you

1. Create/sign in to Oracle Cloud and complete identity/payment verification. Use your own billing details; the agent does not need them.
2. Choose the home region carefully: Always Free resources must be available there. A nearby European region is sensible, but provisioning capacity matters.
3. Confirm that the VM shape, OS image, boot storage and networking choices are Always Free eligible. Check the console estimate and regional/tenancy limits. Trial credit is not proof something remains free after the trial.
4. Enable account notifications and a small budget alert. A budget alert is a notification, not a spending cap. Avoid paid add-ons and keep only the intended VM/storage.
5. Confirm GitHub repository visibility and Actions allowance. Standard hosted runners are free for public repositories; private repositories consume the account's included allowance. Keep paid overages disabled if you require strict zero cost.
6. Create a Tailscale Personal account for private deployment connectivity. Check its current free ephemeral-runner allowance; it currently lists 1,000 ephemeral resource minutes/month. Only the short deployment job should join this network.

Checkpoint: accounts ready; Always Free limits and estimates understood; no VM purchased outside that allocation.

## 2. Provision the Oracle VM — you, with agent guidance

1. Create a compartment such as `happymix` to group the project resources.
2. Create an instance named `happymix-prod` using the Always Free eligible Ampere A1 shape and Ubuntu ARM64 image.
3. Select 1 OCPU, 2 GB RAM and the eligible boot-volume size if available; verify the free badge/estimate again. Do not select a paid substitute when free capacity is unavailable.
4. Create/select a VCN with a public subnet, an internet gateway and an internet route. This provides outbound access to Discord, PandaScore, GHCR, updates and Tailscale.
5. Generate/download the initial administrator SSH key and store it privately. Never commit any private key.
6. Initially allow inbound TCP 22 only from your home public IP `/32`, in the Oracle security list or NSG. Leave application ports closed; this bot initiates outbound connections.
7. Keep outbound connectivity available. Do not introduce a paid NAT gateway for this single public-subnet VM.
8. Record the public IP, image username (normally `ubuntu`), shape and architecture.

From Windows PowerShell, replacing placeholders:

```powershell
ssh -i C:\path\to\oracle-admin-key ubuntu@PUBLIC_IP
```

On the VM:

```bash
uname -m
free -h
df -h /
```

Checkpoint: administrator SSH works; `uname -m` reports `aarch64`. If it reports `x86_64`, use AMD64 builds instead throughout the plan.

## 3. Prepare the OS and container runtime — you/agent

On the VM:

```bash
sudo apt-get update
sudo apt-get upgrade -y
sudo apt-get install -y ca-certificates curl sqlite3
```

Install Docker Engine and the Compose plugin using Docker's official Ubuntu apt-repository instructions linked below. Those instructions support both ARM64 and AMD64; avoid installing several competing Docker distributions.

```bash
sudo systemctl enable --now docker
sudo docker version
sudo docker compose version
sudo docker run --rm hello-world
```

If an OS update requires a reboot, reboot now, reconnect and verify Docker starts automatically. Do not install a .NET SDK or a build runner on this VM; CI builds the image elsewhere.

Checkpoint: Docker daemon and Compose work after reboot.

## 4. Establish private CI-to-VM networking — you/agent

GitHub-hosted runner addresses change. A firewall rule permitting only your home IP will not let those runners deploy. Use the following private overlay rather than assuming GitHub has one fixed runner address.

1. Install Tailscale on the VM using its official Linux instructions; join your Personal tailnet and name the machine `happymix-prod`.
2. Install Tailscale on your PC and join the same tailnet.
3. Tag the VM `tag:happymix-prod` using your tailnet administration account.
4. Create a separate CI identity tagged `tag:happymix-ci`. Configure a GitHub Action-compatible OAuth client authorized to create ephemeral devices with only that tag, using Tailscale's GitHub integration guide.
5. Update the tailnet access policy to allow the CI tag to reach only the production tag on TCP 22. Retain administrator access from your own identity. Remove a broad default allow-all rule if it would defeat that restriction.
6. This plan uses ordinary OpenSSH over the Tailscale network, not Tailscale SSH. The deployment still authenticates with a dedicated SSH key.
7. Confirm SSH from your PC to the VM's Tailscale IP. Ensure the OS firewall permits SSH on the Tailscale interface; do not overwrite existing Oracle networking rules blindly.
8. After verifying a second working admin session and retaining Oracle console recovery access, remove the public inbound SSH rule. The VCN can still provide outbound internet connectivity.

Checkpoint: administrative SSH works over the tailnet; no public application port is open; CI access will be limited to the intended VM's SSH port.

## 5. Create the deployment identity and folders — you/agent

1. Create a Linux user `happymix-deploy` with a home directory and no password-based login.
2. Give that account the Docker access needed to operate this service. Docker-group membership is effectively root access, so this must be a dedicated trusted deployment identity, not a general-purpose account.
3. On your PC generate a separate Ed25519 deployment key, distinct from the Oracle admin key. CI needs an unattended key; keep it dedicated to this project and protect it as a GitHub secret.

```powershell
ssh-keygen -t ed25519 -f "$env:USERPROFILE\.ssh\happymix-actions" -C "happymix-github-actions"
```

4. Install only its `.pub` contents in the deployment user's `~/.ssh/authorized_keys`. Apply directory/file permissions 700/600. Disable forwarding and PTY allocation for this key; it only needs noninteractive deployment commands.
5. Create `/opt/happymix` owned by the deployment user. Keep `/opt/happymix/backups` private, with enough free space for a small rolling backup set.
6. Obtain the VM's OpenSSH host public key/fingerprint through the already trusted admin connection or console. Store the verified `known_hosts` entry for the Tailscale hostname/IP. Do not treat an unauthenticated `ssh-keyscan` in the deployment workflow as host verification.

Checkpoint: the dedicated user can SSH over Tailscale and execute Docker commands; the host key is verified.

## 6. Prepare production files in this repository — agent implementation task

Create these files in a reviewed PR; the current repository has development Compose but no production workflow:

| File | Responsibility |
| --- | --- |
| `compose.production.yaml` | One image-based bot service; production environment; explicit stable SQLite volume; restart policy; bounded logs |
| `deploy/bot.env.example` | Names of required runtime values only; no real credentials |
| `deploy/deploy.sh` | Pull a specified image digest, serialize deployment, stop old bot, back up database, start new bot, verify and record result |
| `deploy/backup.sh` | Consistent SQLite snapshot with bounded retention |
| `.github/workflows/ci.yml` | PR build/tests with no production credentials |
| `.github/workflows/deploy.yml` | Trusted main-branch build/publish/deploy, plus manual retry |

Production Compose must:

- use `image: ${HAPPYMIX_IMAGE}` instead of building on the VM;
- read runtime values from `/opt/happymix/bot.env` without printing them;
- set `DOTNET_ENVIRONMENT=Production` and `Storage__DatabasePath=/app/data/happymix.db`;
- mount a volume with an explicit fixed name, e.g. `happymix-data`, to `/app/data` so changing the Compose directory/project does not silently create a new database;
- use a fixed Compose project name, e.g. `happymix`;
- use `restart: unless-stopped`, a shutdown grace period, and bounded container log rotation;
- publish no host ports and run just one replica;
- retain the existing image's non-root user and writable data-directory ownership;
- avoid mounting the development appsettings file.

Validate Compose locally without connecting the bot to production. Keep the production token file ignored and outside the repository. Review the pinned SDK/runtime image versions deliberately; the current Dockerfile pins SDK 10.0.102 and runtime 10.0.2. CI must use the SDK selected by global.json, not assume the latest preinstalled runner SDK will satisfy it.

Checkpoint: production configuration validates; tests pass; secrets remain absent from Git/image layers.

## 7. Store runtime secrets on Oracle — you

Create `/opt/happymix/bot.env` with a terminal editor and permissions 600. Do not put token values in command history, screenshots or chat.

```dotenv
Discord__Token=REPLACE_LOCALLY
Discord__AnnouncementChannelId=REPLACE_LOCALLY
PandaScore__Token=REPLACE_LOCALLY
Storage__ReplayExistingResultsOnFirstRun=false
```

Use the same Discord channel ID on updates to retain the intended history scope. Do not copy the developer settings file into the image. GitHub only needs credentials for deployment; it does not need the bot or PandaScore token.

Checkpoint: VM has runtime credentials; repository and workflow logs do not.

## 8. Configure GHCR image access — you/agent

Image name: `ghcr.io/tta19764/happymixdiscordbot` (lowercase).

1. Publish using the workflow's short-lived `GITHUB_TOKEN` with `contents: read` and `packages: write` permissions.
2. Link the package to this repository. Tag each build by commit SHA and retain the resulting digest.
3. Choose visibility explicitly. A public image can be pulled anonymously; that is only appropriate if publishing the application image is acceptable to you. Repository visibility does not automatically settle package visibility.
4. For a private image, create a read-only package credential suitable for GHCR on the VM (GitHub currently documents a classic PAT with `read:packages` for external clients). Authenticate as the deployment user with `docker login ghcr.io --password-stdin`; avoid placing the token on the command line. Rotate it before expiry.
5. Keep registry credentials on the VM separate from the workflow's publish token. A workflow token is not a permanent server credential.

Checkpoint: the deployment user can pull the intended package without write access to the registry.

## 9. Add GitHub deployment secrets — you

Under repository Settings -> Secrets and variables -> Actions, add:

| Name | Value |
| --- | --- |
| `ORACLE_SSH_HOST` | Stable VM Tailscale IP or hostname |
| `ORACLE_SSH_USER` | `happymix-deploy` |
| `ORACLE_SSH_PRIVATE_KEY` | Dedicated deployment private key, including delimiters/newlines |
| `ORACLE_SSH_KNOWN_HOSTS` | Independently verified OpenSSH known-hosts entry |
| `TS_OAUTH_CLIENT_ID` | Restricted Tailscale GitHub integration client ID |
| `TS_OAUTH_SECRET` | Matching client secret |

Use GitHub environment secrets/protection if your repository visibility and plan support them. For a private repository on GitHub Free, do not assume environment secrets or required-reviewer protection is available; repository Actions secrets can be used with tightly scoped workflow triggers. `GITHUB_TOKEN` is supplied automatically and is not a manually created secret.

Checkpoint: every named secret exists; PR tests have no production secrets and no deployment step.

## 10. Implement and verify CI — agent, then you review

For pull requests and main commits:

1. Check out the code with a reviewed action version pinned to a commit SHA.
2. Install the SDK selected by `global.json` using setup-dotnet.
3. Restore the solution.
4. Build in Release with warnings treated as configured by the repository.
5. Run the full tests, including disk-backed SQLite migration/restart tests.
6. Upload test results when failures occur with short artifact retention.

No running Discord bot is required for tests. Do not run deployment with untrusted PR code or use `pull_request_target` to execute a contributor's changes with secrets. Protect main with required checks if available on your plan.

Checkpoint: a test PR passes CI; an intentionally failing test stops the workflow without publishing or deployment.

## 11. Implement build/publish and deployment — agent

Only trusted pushes to main and explicitly permitted manual runs may deploy. Set one production concurrency group and do not cancel an in-progress deployment halfway through migration/backup.

Build/publish job:

1. Depend on successful tests, either in this workflow or a securely validated completed CI workflow.
2. Build for `linux/arm64` using Buildx with QEMU on an x86 hosted runner, or a GitHub ARM hosted runner if available under your repository's free allowance. For an x86 Oracle VM use `linux/amd64` instead.
3. Publish to GHCR using GITHUB_TOKEN. Include commit/repository metadata.
4. Pass the immutable pushed image digest to the deployment job. Do not deploy whatever happens to be tagged latest.

Deployment job:

1. Join Tailscale as an ephemeral runner using its official GitHub action and restricted CI tag. Keep build/test outside this job to minimize ephemeral minutes.
2. Install the SSH key and verified known-hosts data into temporary files with restrictive permissions.
3. Copy the reviewed Compose/deployment files to a staging release directory on the VM. Do not overwrite `bot.env` or the data volume.
4. Execute the deployment script for the exact image digest. Use both workflow concurrency and a server-side lock so manual and automated deployment cannot overlap.
5. Pull the replacement image **before** stopping the old bot; a failed pull should not cause downtime.
6. Stop the old bot gracefully and confirm it has stopped. Do not scale up or start a parallel replica.
7. Take a consistent pre-migration database backup. On first deployment there may be no database yet; treat that case explicitly.
8. Start the new container with the same named volume and runtime secrets. Startup applies EF migrations before Discord connects.
9. Check restart count, container state and startup logs over a bounded observation period. Require evidence of Discord connection and successful provider synchronization, not only a running container. Never log secrets while doing this.
10. Record the deployed commit/digest, previous digest and backup path only after success. A failure should fail the GitHub job and retain diagnostic logs without claiming success.

Checkpoint: a trusted commit deploys exactly the tested image; old/new publishing processes never overlap.

## 12. First deployment and acceptance — you/agent

1. Before cloud startup, stop any local HappyMix instance using the same Discord token/channel. Two independent databases do not coordinate deduplication.
2. Trigger the production workflow manually for the main commit and watch its logs.
3. Verify the container and logs on the VM:

```bash
cd /opt/happymix
docker compose -p happymix -f compose.production.yaml ps
docker compose -p happymix -f compose.production.yaml logs --tail=100 bot
```

4. Use `/ping` in Discord. Check that only one bot instance is running and the channel is accessible.
5. Confirm PandaScore requests succeed. A successful empty schedule is valid if no followed team has a match; wait for a genuine matching event to verify actual notifications. Do not fabricate results in the production channel.
6. Restart the container and verify recorded schedule/results are not repeated. Check that the database file remains on the same volume.
7. Reboot the VM and verify Docker/bot recovery. Do not manually stop the service before this test, since `unless-stopped` intentionally respects a manually stopped container.
8. Turn off your PC and confirm the remote bot remains responsive.

Checkpoint: independent operation, persistence and reboot recovery are demonstrated.

## 13. Backups and rollback — agent implements, you retain a copy

1. Before every deployment, stop the old publisher and snapshot the SQLite database; keep several recent snapshots with UTC timestamps and the corresponding image digest.
2. Add a daily backup using SQLite's backup mechanism (or stop the bot for an offline copy). Never assume copying only an active `.db` file is consistent when WAL/sidecars are involved.
3. Keep a copy **off the Oracle VM** on a defined schedule. Initially you can download it to your PC over the admin connection; automate a separate free-tier destination only after choosing and checking its limits. Backups only on the reclaimed VM are insufficient.
4. For rollback, stop the new bot and deploy the recorded previous digest. If a new schema is incompatible, restore the matching pre-deployment database snapshot while stopped; record the possible loss of messages/history since that snapshot.
5. Do not automatically roll back only the image after a migration unless schema compatibility is established. Forward fixes can be safer than restoring old history, which can cause notifications to replay.
6. Keep at least one known-good image and its database backup until a newer deployment has been verified. Limit old image/log/backup retention to avoid filling the free boot disk.
7. Perform a restore rehearsal against a separate temporary database/container with no real Discord credentials before marking recovery verified.

Checkpoint: you know which image/database pair to restore and have an off-VM backup.

## 14. Finish Day 2 and define the daily routine — you/agent

Mark Day 2 done only after actual remote deployment, cost eligibility, persistence and reboot checks pass. A written workflow or a successful image build alone is not completion. Update docs/ROADMAP.md and regenerate docs/progress.html.

Your normal routine then becomes: review agent changes -> merge after green tests -> check the deployment job -> test one user-facing flow. Monitor Actions usage, Tailscale ephemeral minutes, free disk space and Oracle account notifications. Oracle can reclaim idle instances; do not generate artificial load to defeat that policy.

Suggested short sessions:

| Session | Your activity | Agent activity |
| --- | --- | --- |
| 1 | Oracle account, region and free VM selection | Check choices and prepare production files |
| 2 | First SSH access and Tailscale login | Install/configure runtime and private deployment path |
| 3 | Add keys and secrets yourself | Implement/test CI and image publishing |
| 4 | Observe first deployment and use /ping | Verify logs, persistence and reboot behavior |
| 5 | Download recovery backup and review billing | Rehearse recovery and update progress |

Account verification and free capacity provisioning can exceed a 20-minute session or require waiting; keep those separate from coding tasks.

## Official references checked for this plan

- Oracle Always Free resources: https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm
- Oracle Free Tier FAQ and capacity caveats: https://www.oracle.com/cloud/free/faq/
- Docker installation on Ubuntu: https://docs.docker.com/engine/install/ubuntu/
- Docker multi-platform GitHub builds: https://docs.docker.com/build/ci/github-actions/multi-platform/
- GitHub Container Registry access: https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry
- GitHub Actions billing: https://docs.github.com/en/billing/concepts/product-billing/github-actions
- GitHub container storage/bandwidth billing: https://docs.github.com/en/billing/concepts/product-billing/github-packages
- GitHub environment availability: https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments
- Tailscale GitHub Action authentication: https://tailscale.com/docs/integrations/github/github-action
- Tailscale Personal plan and ephemeral usage limits: https://tailscale.com/pricing

Costs, quotas, UI labels and account-specific eligibility can change. Verify the relevant console/plan before enabling a resource.
