# Operations

How to install, run and maintain the orchestrator on a Linux host with Docker Engine. The
reference host is `gh-runner-2` (`docs/ai/core.md`); nothing below depends on it.

## Requirements

- Linux with **Docker Engine** (not Docker Desktop: it publishes random ports only on the
  Windows side, and Testcontainers cannot reach them). cgroup v2.
- The GitHub App and its private key ([maintainer-setup.md](maintainer-setup.md)), the key at
  `/etc/gh-runners-orchestrator/github-app.pem`, mode `0400`, owner `1654`.
- Nothing else of value on the host: every job can reach the Docker socket
  ([ADR 0006](adr/0006-security-model.md)).

## Install

As a user in the `docker` group:

```bash
mkdir -p ~/gh-runners-orchestrator/config && cd ~/gh-runners-orchestrator
base=https://raw.githubusercontent.com/thiagoluga/gh-runners-orchestrator/main/deploy
curl -fsSLO "$base/compose.yaml"
curl -fsSL -o .env "$base/.env.example"
curl -fsSL -o config/orchestrator.json "$base/config/orchestrator.example.json"
curl -fsSL -o config/repositories.json "$base/config/repositories.example.json"
```

Then edit:

- `.env`: `DOCKER_GID` from `stat -c %g /var/run/docker.sock`; `ORCHESTRATOR_TAG` (a
  `sha-<commit>` tag pins a build, `main` follows the latest one).
- `config/orchestrator.json`: owner, App id, labels, runner image, and **slots**. Give every
  slot its own cores (`cpuset`, never a CPU quota) and keep the sum of slot memory well
  below the host's, leaving cores and memory for the host and for the containers jobs start.
- `config/repositories.json`: the repositories the pool serves.

Start it and read the first cycles:

```bash
docker compose pull
docker compose up -d
docker compose logs -f --tail 20
```

Expect `Installation token renewed …` and then one `Cycle: …` line per poll interval.

## Several hosts

Each host runs its own orchestrator with its own slots, App key files and the same
repository list ([ADR 0013](adr/0013-peer-orchestrators-on-several-hosts.md)). Add to
`config/orchestrator.json` on every host:

```jsonc
"Host": "github-runner",                       // this host; differs on each host
"PoolHosts": [ "gh-runner-2", "github-runner" ], // identical, same order, on every host
"TakeOverAfter": "00:01:00"                     // optional; at least two poll intervals
```

- A queued job belongs to `PoolHosts[job id mod N]`. Another host serves it only after it
  waited `TakeOverAfter`, so a job reaches a free slot elsewhere within that delay when
  its owner is full or down.
- Runner names carry the host (`orch-<host>--…`) and each host deletes only its own
  registrations. Runners named before the switch (`orch-<owner>-…`) belong to the first
  host of the list: put the existing host first.
- `MaxConcurrent` is global: online runners of other hosts count.
- Each host polls GitHub, so API use per installation grows with the hosts; watch
  `ghro_rate_limit_remaining` on each.
- Change `PoolHosts` on every host, then restart them. Before removing a host, stop its
  orchestrator and let its runners finish.

## Multiple accounts

See [multi-account.md](multi-account.md). Account/installation changes need a restart and
explicit deployment approval. Existing `Owner` installations require no configuration
change to run the new binary. Repository-list edits still reload without restarting.

## Enable or disable a repository

1. **Enable**: GitHub → the App → **Install App** → configure → add the repository (private,
   owned by an explicitly authorized account). Then add it to `config/repositories.json`. The orchestrator
   reloads the file on the next cycle; no restart. Aim the repository's jobs at the pool
   label (`runs-on: vm-pool`).
2. **Disable**: remove it from `config/repositories.json`. Jobs already running finish; no new
   runners start; idle ones are removed after the idle timeout. Then remove it from the App
   installation.

Slots share the host network and Docker daemon. Give `MaxConcurrent: 1` to a repository
whose tests assume a host of their own. **Two .NET Aspire applications must never run at
the same time on one host**, whatever their repository: the orchestrator (DCP) of the one
started first stops and deletes its containers a few seconds after the second starts
([#35](https://github.com/thiagoluga/gh-runners-orchestrator/issues/35), measured with the
`aspire-isolation` probe of runner-pool-sandbox). saas-foundry, the only Aspire repository
on the pool, runs at `MaxConcurrent: 1`. Before enabling a second repository whose jobs
start Aspire AppHosts, the pool needs a way to keep such jobs exclusive (not built yet).

A listed repository that is public, owned by an unauthorized account or not covered by the installation
is refused with a `Repository … refused: …` warning. When the App is installed on a repository
that is already listed, the token picks it up within 5 minutes: while a listed repository is
outside the token, the orchestrator scopes a new one at that interval (log reason
`a listed repository is outside the token`) and revokes the one it replaces.

## Logs

`docker compose logs` shows one line per cycle and one per runner event:

| Line | Meaning |
|---|---|
| `Runner … started for … in slot-n` | a just-in-time runner was created for a queued job |
| `Runner … finished with exit code …` | its container ended and was removed |
| `… picked no job before the idle timeout; removed` | the job it was made for was cancelled or taken by another runner |
| `… had no runner on GitHub for two cycles; removed` | a container whose runner was deleted |
| `Offline runner … had no container; deleted` | a registration left behind by a crash |
| `Rate limit low …` | the API budget is under 10%; demand is skipped until it resets |

No log line ever contains a token, the key or a JIT configuration.

## Health and metrics

The reference deployment serves two read-only pages on the host's loopback
(`127.0.0.1:9464`, `STATUS_PORT` in `.env`; `Orchestrator:StatusPort` 0 turns it off):

- `GET /healthz`: `200 ok` while cycles complete; `503` before the first cycle or when no
  cycle completed in the last three poll intervals plus a minute. A watchdog can restart
  the orchestrator on 503: `curl -fsS http://127.0.0.1:9464/healthz || docker compose restart`.
- `GET /metrics`: Prometheus text format. `ghro_healthy`, `ghro_cycles_total`,
  `ghro_cycle_failures_total`, `ghro_last_successful_cycle_timestamp_seconds`,
  `ghro_slots`, `ghro_slots_busy`, `ghro_runners_launched_total`,
  `ghro_runners_finished_total`, `ghro_launch_failures_total`, `ghro_rate_limited`,
  `ghro_blocked` (a managed container without a configured slot stops launches), and per
  repository or account `ghro_queued_jobs`, `ghro_waiting_runners`,
  `ghro_rate_limit_remaining`. Counters restart at zero with the orchestrator.

The pages hold counters and repository names, never a token or a runner configuration.
Keep the port off public interfaces: repository names are private.

## Update

- **Orchestrator**: set `ORCHESTRATOR_TAG`, then `docker compose pull && docker compose up -d`.
  Running jobs are not affected: runners are separate containers, and the new orchestrator
  rebuilds its view from their labels.
- **Runner image**: change `RunnerImage` in `config/orchestrator.json` and restart. New runners
  use the new image; the host pulls it on the first launch. The `runner-version` workflow opens
  a pull request when GitHub releases a new runner; the orchestrator also warns in its log when
  the configured runner is older than the latest release.
- **Runner image, same tag**: a change to `runner/` that keeps the runner version republishes
  the same tag (for example `2.338.2`), and the host pulls only a tag it does not have. Pull it
  once: `docker pull ghcr.io/thiagoluga/gh-runners-orchestrator-runner:<tag>`. The next runner
  uses it; no restart is needed.

## Rotate the App key

1. GitHub → the App → **Private keys** → **Generate a private key**.
2. Copy it to the host as in [maintainer-setup.md](maintainer-setup.md#copy-the-key-to-the-host)
   (it replaces the file).
3. `docker compose restart`, and check `Installation token renewed` in the log.
4. Delete the old key on the App page.

## Containers left by jobs

Containers that a job starts through the host Docker (PostgreSQL, Mailpit, Testcontainers) are
siblings of the runner, not children. Testcontainers removes its own after a normal exit; after
a runner is killed they can remain. The orchestrator does not remove containers it did not
create. Find them with:

```bash
docker ps -a --filter "label=org.testcontainers=true" --format '{{.ID}} {{.Image}} {{.Status}}'
docker ps -a --format '{{.ID}} {{.Image}} {{.Status}} {{.Labels}}' | grep -v orchestrator.managed
```

Remove only what you recognize as left over.

## Host reboot

The orchestrator has `restart: unless-stopped` and comes back with Docker. Runner containers
interrupted by the reboot come back as exited: the first cycle removes them and deletes their
registrations; the jobs they ran fail on GitHub and can be re-run. Queued jobs are picked up
again as soon as the first cycle runs.

## Stop

`docker compose down` stops the orchestrator only. Runners already running finish their job and
stay as exited containers until the orchestrator runs again.

## Multiple private Apps

See [multiple-apps.md](multiple-apps.md) for the App registry, explicit account bindings,
legacy configuration compatibility, key mounts and deployment/rollback approval gates.
The original App can remain private; do not change its visibility for this option.
