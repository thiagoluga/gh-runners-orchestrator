# Runner pool verification

Phase 3 ([#4](https://github.com/thiagoluga/gh-runners-orchestrator/issues/4)) run on
`gh-runner-2` on 2026-11-15, with the deployment of [deploy/](../deploy/compose.yaml)
(orchestrator image built from the pull request, `IdleRunnerTimeout` lowered to 1 minute for
the test) against `thiagoluga/runner-pool-sandbox`.

| Case | How | Result |
|---|---|---|
| Start from the deployment files | `compose.yaml`, `.env`, `config/` as in [operations.md](operations.md) | token renewed, one `Cycle` line per 20 s; idle cycles cost 0 API requests |
| Job cancelled before pickup ([#15](https://github.com/thiagoluga/gh-runners-orchestrator/issues/15)) | dispatch, cancel as the container appeared | runner never got the job; removed 78 s after start (`picked no job before the idle timeout`), registration deleted |
| Repository disabled, then enabled ([#14](https://github.com/thiagoluga/gh-runners-orchestrator/issues/14)) | empty `repositories.json`, dispatch, restore the file | no runner while disabled (3 cycles); runner started on the first cycle after the file came back; no restart |
| Orphan container | a container with the managed labels and no runner on GitHub | removed after two cycles (`had no runner on GitHub for two cycles`) |
| Orphan runner | runner container removed by hand before its job | offline registration deleted on the next cycle; the job got a new runner and ran |
| Orchestrator restart mid-job | `docker compose restart` while a job ran | the job kept running; the new process rebuilt the pool from labels (`1/2 slots taken`) |
| Runner killed mid-job | `docker kill` of the runner container | exited container removed with exit code 137; GitHub kept the runner busy until it gave the job up, refused the delete (422), then removed the registration itself |

## After the merge

| Case | How | Result |
|---|---|---|
| Fresh install from `main` ([#16](https://github.com/thiagoluga/gh-runners-orchestrator/issues/16)) | the verification deployment and its images removed, then only the commands of [operations.md](operations.md) (`curl` from `main`, `DOCKER_GID`, `docker compose pull && up -d`) | `ghcr.io/thiagoluga/gh-runners-orchestrator:main` pulled without login; token renewed; cycles running |
| Host reboot mid-job | `sudo systemctl reboot` 20 s into a long job | the orchestrator came back with Docker; the interrupted runner (exit code 143) was removed on the first cycle; the job failed on GitHub; a `smoke` run right after went green in 30 s; no registration left |
| Runner version workflow ([#17](https://github.com/thiagoluga/gh-runners-orchestrator/issues/17)) | dispatched with `base` set to a branch pinning runner 2.337.0 | detected 2.337.0 < 2.338.2, downloaded the release, computed the same SHA-256 as the known-good pin, pushed `chore/runner-2.338.2`; **opening the pull request was refused** ("GitHub Actions is not permitted to create or approve pull requests") |

## saas-foundry CI on the pool

[#18](https://github.com/thiagoluga/gh-runners-orchestrator/issues/18), on 2026-11-15:
`CI_RUNNER=vm-pool` in saas-foundry, the App installed on it, the orchestrator from `main`.
The same commit (`9598425`, saas-foundry PR #11) ran on `vm-dev-1` first, then was re-run on
the pool. Memory sampled every 5 s with `docker stats`.

| Job | `vm-dev-1` | pool, run 1 | pool, run 2 |
|---|---|---|---|
| foundation container images | 1:51 | 0:38 | 0:39 |
| generator build and test | 0:55 | 1:02 | 1:02 |
| generate, build and test a real project | 5:11 | 4:58 | 4:51 |
| foundation build and test | 3:50 | 3:25 | 3:22 |
| secret-scan | 0:13 | 0:08 | 0:08 |
| SonarCloud analysis and Quality Gate | 4:42 | 1:53 | 1:52 |
| **Whole run** (wall clock) | **17:03** | **13:58** | **14:00** |

Both pool runs were green with Sonar, with saas-foundry at `MaxConcurrent: 1`, so the jobs
ran one after another as on `vm-dev-1`.
- **Memory peak:** 3.1 GiB in a runner container (limit 16 GiB). Sibling containers stayed
  under 60 MiB (Postgres) and 15 MiB (Mailpit). The host peaked at 4.3 GiB used of 40 GiB.
- **Slot waits:** about 20 s from one job's end to the next job's start: one poll interval,
  plus the container start.

**Two slots for saas-foundry are not safe yet.** At `MaxConcurrent: 2`, the test
`HostLifecycleTests.Start_WithPersistentVolume_PreservesDataAcrossContainerReplacement`
failed twice in three runs. Each failure came when the foundation and generated-project
hosting tests overlapped in the two slots, which share the host network and Docker daemon.
Tracked in [#35](https://github.com/thiagoluga/gh-runners-orchestrator/issues/35). The memory
peak at two slots was 3.2 GiB per runner and 6.2 GiB on the host.

Sonar on a `main` push of saas-foundry fails with `CE Task finished abnormally`. It fails the
same way on `vm-dev-1` (run 43727251273), so the cause is the Sonar project, not the pool. Sonar
on a manually dispatched branch is refused by the plan ("not allowed to access data from non
main branches").

## vm-dev-1 retired

[#19](https://github.com/thiagoluga/gh-runners-orchestrator/issues/19), on 2026-11-15, after
Thiago's confirmation:

- `RunnerLabels` on `gh-runner-2` became `["vm-pool", "vm-dev"]`, then the orchestrator was
  restarted while idle.
- The three `vm-dev` registrations of saas-foundry (`vm-dev-1` offline, `vm-dev-2` and
  `vm-dev-3` offline since earlier) were deleted.
- saas-foundry `CI_RUNNER` went back to `vm-dev`, and its `docs/ci-runners.md` now points here.
- The VM at `192.168.68.50` was already off and did not answer, so its runner container and
  the volumes `saas-foundry-runners_state-1` and `saas-foundry-runners_work-1` are still on
  its disk. They are harmless without a registration; remove them, or the VM, when it is
  next up.

The first saas-foundry runs on the pool after `vm-pool` failed in `foundation container images`:
the runner image had no Docker Compose plugin. It was added with Buildx
([#38](https://github.com/thiagoluga/gh-runners-orchestrator/pull/38)) and pulled on the host.
Then saas-foundry PR #12 (run 43755390367, attempt 2) ran green with `CI_RUNNER=vm-dev`, Sonar
included: 13:32 wall clock.

## Two repositories sharing the slots

[#13](https://github.com/thiagoluga/gh-runners-orchestrator/issues/13), on 2026-11-15, with
`thiagoluga/runner-pool-sandbox-2` (private) next to the first sandbox, both at
`MaxConcurrent: 2`.

| Case | How | Result |
|---|---|---|
| App installed on a listed repository | listed first (`refused: not readable by the App installation`), then the maintainer added it to the installation | taken in at the next rescope, 5 minutes after the last token (`a listed repository is outside the token`), no restart |
| Both repositories queue at once | `capacity` (3 legs of 120 s) dispatched in both within seconds | each round one slot per repository: 20:19:31, 20:21:51 and 20:24:11 for both; neither got both slots while the other waited; no container left |

## Not verified

- **The version workflow opening its pull request**: needs the repository setting
  *Settings → Actions → General → Allow GitHub Actions to create and approve pull requests*,
  a maintainer decision.

## Observed

Each runner start lowered the installation's API budget by about 35 requests in the next
cycle, while the orchestrator itself makes about 5. The rest is most likely the runner's own
calls, counted on the same installation; not investigated further. At 5,000 per hour this
allows well over 100 runner starts per hour.

## Multi-App binary, legacy configuration acceptance

On 2026-11-16, after the maintainer approved stage 1, the orchestrator was recreated
from merged commit `3a1e352e39ff266fd557efc530dcf84dcc515e49`
([PR 41](https://github.com/thiagoluga/gh-runners-orchestrator/pull/41)). The immutable image
is `ghcr.io/thiagoluga/gh-runners-orchestrator@sha256:b0047fbc24917d9f26f6b719710cac506c1793bbd92ba2307d94be63e9bb577f`.

- The activation verified the previous image/deployment identity, found no active managed
  runners, backed up configuration and used an explicit image-only Compose override
  (`activation-3a1e352.yaml`). JSON, base Compose, environment pins, key mounts, repositories,
  slots, labels and saas-foundry's concurrency cap stayed unchanged. Byte comparisons of
  both JSON files passed.
- Startup renewed the existing installation token and completed cycles for all three
  configured thiagoluga repositories with two global slots. No token/key/JIT value was read
  or printed by the agent.
- The existing private sandbox [smoke run 43861024304](https://github.com/thiagoluga/runner-pool-sandbox/actions/runs/43861024304)
  passed on sandbox commit `9573ec293cfdb6ef4ffd355db674404bbe53f060`, label `vm-pool`.
  Its job completed in 6 seconds on runner
  `orch-thiagoluga-runner-pool-sandbox-slot-1-c384a398`, runner version `2.338.2`.
- The job measured 5 cores (`0-4`), memory limit `17179869184` bytes (16 GiB), and
  `nofile=65536`. Its JIT-environment check printed `not present`; Docker hello-world passed.
  These are configuration/smoke observations, not a load or performance comparison.
- At 11:49:20 UTC the orchestrator reported the runner finished with exit code 0 and its
  container was removed. The following cycle returned to `0/2` slots taken; the managed
  container listing was empty.

Backup/rollback metadata stays on the host at
`/home/ci/gh-runners-orchestrator/backups/20261116-3a1e352-legacy/`.
The previous immutable image is
`ghcr.io/thiagoluga/gh-runners-orchestrator@sha256:bc8ddf9447e3fd6487fec682fdc268c390e8add88a2c2bafb7df271008b990fd`.
Use the explicit stage-1 override until the separately approved permanent deployment pin
is applied; a plain `docker compose up` would use the original mutable image pin.

This accepts the new binary with legacy thiagoluga configuration only. No maozinha account,
App key, repository-list entry or consumer workflow was activated. Two-App real-host
acceptance remains pending key provisioning, verified installation metadata and a separately
reviewed/approved registry/Compose configuration diff.

## Separate private Apps activated

On 2026-11-16, after explicit stage-2 approval, the reviewed registry configuration was
applied to the same accepted `3a1e352` image. No new binary or runner image was installed.

- Authenticated metadata verified private App `maozinha-runner-pool`, App ID `5783289`,
  owned by maozinha; installation `182542055` is selected-repository, not suspended, and
  covers **only private maozinha/insurapp**. App and installation permissions were exactly
  Actions read, Administration write and Metadata read; no events. The authenticated App
  lookup succeeded while its unauthenticated public lookup returned 404.
- The new key was provisioned host-only at
  `/etc/gh-runners-orchestrator/github-app-maozinha.pem`, UID/GID `1654:1654`, mode `0400`.
  Only filesystem metadata was displayed; secret values were never printed. The private
  incoming staging file was removed. The original thiagoluga key was left in place.
- Base Compose now pins the accepted immutable image directly and mounts both keys
  read-only. Inspection showed the orchestrator running with restart count 0 and both
  secret mounts at `RW=false`. The temporary stage-1 image override is no longer required
  for normal `docker compose` commands.
- All existing repositories/caps, labels and two global slots were preserved. Only
  maozinha/insurapp was appended, at `MaxConcurrent: 1`. The existing thiagoluga installation
  is `182223102`; each account selects its own App, installation and key path.
- At 12:06:02 and 12:06:04 UTC startup logs confirmed separate token renewals for
  installations `182223102` and `182542055`. Subsequent cycles included all four qualified
  repositories and separate thiagoluga/maozinha rate budgets. No repository refusal appeared
  in the inspected startup/stable cycles.
- Regression [smoke run 43863527100](https://github.com/thiagoluga/runner-pool-sandbox/actions/runs/43863527100)
  passed on the same sandbox commit `9573ec293cfdb6ef4ffd355db674404bbe53f060`, label `vm-pool`,
  job duration 7 seconds. Runner
  `orch-thiagoluga-runner-pool-sandbox-slot-1-c6485a67` measured 5 cores (`0-4`),
  `17179869184` bytes of memory limit and `nofile=65536`; JIT was absent from the job
  environment and Docker hello-world passed.
- At 12:07:00 UTC the orchestrator recorded exit code 0 and automatic container removal.
  At 12:07:08 and 12:07:27 UTC stable cycles returned to `0/2` slots; the later managed
  container listing was empty.

The stage-2 backup is
`/home/ci/gh-runners-orchestrator/backups/20261116-3a1e352-multi-app/`.
Rollback must drain/reap maozinha resources with its binding/key still available, verify no
managed registrations remain through that App, then restore the legacy configuration with
the saved stage-1 image override. No unrelated resource, key, image or volume is removed.
Rollback commands were prepared and syntax-checked, not executed.

`maozinha/insurapp` remains private and `CI_RUNNER` remains `gh`. No consumer workflow was
modified, dispatched or cancelled. Two pre-existing queued consumer runs were observed
without materialized jobs. No artifact/cache/storage policy or GitHub spending was changed.

This verifies both App identities, separate startup tokens, repository admission/stable
polling and preservation of thiagoluga execution/cleanup. A real maozinha JIT/job, two-account
simultaneous load, restart during a maozinha job, and hourly renewal/failure acceptance
remain pending. Unit-test coverage is not substituted for those live scenarios. Insurapp
onboarding and cross-project interference analysis remain a separate task.

## Live Insurapp execution and cross-App overlap

On 2026-11-16, after explicit approval, Insurapp's repository `CI_RUNNER` was temporarily
set to `vm-pool`. No merge, application deployment, App permission change or orchestrator
restart was performed. The coordinator kept image digest `b0047fbc24917d9f26f6b719710cac506c1793bbd92ba2307d94be63e9bb577f`
and restart count 0. The selector was restored to `gh` after each controlled run.

First [Insurapp run 43910547807](https://github.com/maozinha/insurapp/actions/runs/43910547807)
executed commit `e7ef6ea17466d0e65cf8267c55c0f49edf572926`. Four separate ephemeral runners,
IDs 22-25, ran backend/web tests, container SDK, frontend build and Docker runtime checks.
The repository stayed at one active job while another global slot was available.
The inspected maozinha runner selected App 5783289/installation 182542055, cpuset `0-4`,
memory `17179869184`, host networking and CPU quota/nano-CPU fields 0.

While Insurapp ran, [sandbox smoke 43910945842](https://github.com/thiagoluga/runner-pool-sandbox/actions/runs/43910945842)
passed on the unchanged sandbox commit `9573ec293cfdb6ef4ffd355db674404bbe53f060`.
At 16:45:30 UTC, host inspection captured both accounts concurrently in distinct slots:
maozinha in slot 1 and thiagoluga in slot 2, App 5770327/installation 182223102, cpuset
`5-9`, the same 16 GiB runner limit and no CPU quota. The seven-second smoke confirmed
five logical CPUs, `nofile=65536`, Docker hello-world and no JIT configuration in its job
environment. Its runner was automatically removed at 16:46:00 UTC; Insurapp continued.
This was a light overlap, not a heavy cross-project load or peak-capacity test. Runner
limits do not constrain sibling containers created through the shared Docker daemon.

Logs also showed five independent close-to-expiry renewals for each installation at
approximately 13:01, 13:56, 14:51, 15:46 and 16:41 UTC. Only installation identifiers,
expiry timestamps and renewal reasons were inspected; no credential value was displayed.

The first live run exposed two anonymous PostgreSQL volumes surviving cleanup: Actions
removed its service container without `--volumes`, and the initializer inherited the
PostgreSQL image's unused data VOLUME across the data-preserving restart smoke. Empty
runner and Compose-project inventories alone had missed this storage. The correction in
[Insurapp PR 113](https://github.com/maozinha/insurapp/pull/113) uses tmpfs for both disposable
data mounts, preserves named development/key volumes and adds mount regressions.

Corrected [pool run 43915262671](https://github.com/maozinha/insurapp/actions/runs/43915262671)
executed PR head `7f24072beea635c3920562ee6f8065cfcf2108b3`, using separate maozinha runners
26-29. Disposable-storage assertions, non-root initialization, backend/web tests, format,
container SDK and runtime/migration/restricted-role/restart checks passed. Sonar reported
zero new issues, zero accepted issues and zero security hotspots. The only failed steps
were required frontend/test-result artifact uploads, with the existing storage-quota error.
The customer artifact build/upload after the first frontend upload was skipped; publication
and deployment were not executed. Full consumer CI acceptance therefore remains blocked.

At 17:23:06 UTC the managed runner listing was empty. Authenticated checks confirmed no
managed maozinha registrations remained; the sandbox runner inventory was also empty.
Both corrected-run Compose projects and its transient staging smoke had zero containers
and volumes, the transient network was absent, and no dangling volume created since
17:08 UTC remained. The two unused anonymous volumes from the first run were preserved
pending explicit maintainer approval for their bounded removal. No global prune was run;
Docker image/build caches were retained. `CI_RUNNER=gh` was read back after restoration.
Subsequent cycles at 17:24 and 17:26 UTC reported `0/2` occupied slots.

Observed job durations (minutes:seconds):

| Job | Hosted `e7ef6ea` PR | Pool `e7ef6ea` dispatch | Pool `7f24072` PR |
|---|---:|---:|---:|
| build & test | 12:08 | 5:54 | 5:36 |
| verify container SDK | 12:15 | 3:56 | 3:31 |
| build frontend artifact | 1:15 | 0:45 | 0:45 |
| docker build | 2:39 | 1:42 | 1:19 |

The hosted reference is [run 43892827900](https://github.com/maozinha/insurapp/actions/runs/43892827900).
These are single observations. The dispatch did not wait for the Sonar gate; PR runs did.
Hosted jobs ran concurrently, whereas Insurapp pool jobs were serialized. Hardware,
Docker/dependency cache warmth and artifact failures also affect durations. The corrected
head changes storage and checks, so its sample is not a same-code benchmark. No average,
speedup guarantee, heavy-load isolation or peak-memory conclusion follows from this table.
Orchestrator restart or cancellation during a maozinha job was not tested.

After this verification, the maintainer explicitly approved removal of the two original
anonymous volumes. The bounded cleanup checked both exact names, creation timestamps,
anonymous-volume labels and absence of container references before removing only those
two volumes; its success result was read. The maintainer also requested leaving Insurapp
on the pool: repository `CI_RUNNER=vm-pool` was set and read back. This supersedes the
temporary-test selector state above. Required artifact uploads remain a separate blocker;
no application promotion or production operation was performed.

## Pending-workflow discovery deployment - 2026-11-16

Maintainer merged [#42](https://github.com/thiagoluga/gh-runners-orchestrator/pull/42)
as `b5f1a33972d439030564252cf5b75afae70d30ae` and requested continuation. Post-merge
CI, both published images and CodeQL passed; Sonar reported zero new/accepted issues,
zero new hotspots, 89.3% main new-code coverage and no new duplication.

Pulled the revision-tagged orchestrator image and verified its OCI revision before
replacing only the existing Compose service. The host now pins
`ghcr.io/thiagoluga/gh-runners-orchestrator@sha256:180cf2e22afa0fad2dafa9c601de3d0d2681ee35246f73846ff89686e3316cdb`.
Rollback Compose is `/home/ci/gh-runners-orchestrator/compose.yaml.before-b5f1a33`.
No managed runner was active at replacement. Account bindings, host key files, runner
image, two global slots and per-repository capacity were unchanged.

At 20:58:18 UTC the service started, running with zero restarts. Both App installations
renewed successfully; at 20:58:32 it detected two queued Insurapp jobs inside the pending
workflow and launched maozinha runner 32 in slot 1. This establishes discovery recovery;
full current Insurapp CI/package acceptance remains separate and pending.

## Full consumer package acceptance - 2026-11-16

Insurapp [PR #113](https://github.com/maozinha/insurapp/pull/113), final head
`219616be3737794d964feb59f3fa7692a8169c60`, passed
[CI 43961422587](https://github.com/maozinha/insurapp/actions/runs/43961422587):
SDK 3m40s, frontend 1m19s, runtime Docker 1m19s, backend 6m07s and independent read-only
package acceptance 21s. Hosted-only ClamAV acceptance passed on the same head. No
application deployment/publication job ran. Sonar reported zero new/accepted issues,
zero hotspots, 90.4% new coverage and no new duplication; review threads were empty.
The PR is ready for maintainer review/merge to `test`.

The temporary private prerelease stored both sealed frontends (11,117,130 and
11,129,268 bytes) and test evidence (2,454,360 bytes). The separate contents-read job
actually downloaded all three and validated server digests, source/job identity and
frontend manifests. Store inventory was 13 assets/71,655,640 bytes, within the explicit
100-asset/512-MiB ceiling. Existing CI credentials suffice; no new dependency, App
permission or paid allowance. Current variables remain `CI_RUNNER=vm-pool`,
`CI_ARTIFACT_STORE=release`, `CI_ARTIFACT_RELEASE_ID=447382928`.

At 21:49:07 UTC the pool returned to 0/2. Verified no managed runner container or
maozinha managed registration remained; the service was running with zero restarts.
The accepted run left no CI container, volume or network. An older canceled run,
`43949007021`, left only unused `insurapp-sdk-43949007021-1_local-config` and network
`insurapp-sdk-43949007021-1_default`. Both have matching project labels and creation
at 20:58:40 UTC; no container references/endpoints exist. These two resources remain
untouched pending explicit bounded cleanup approval. No broad prune is proposed.

The maintainer subsequently approved exactly those two removals. Rechecked immutable
network ID, creation times, project labels and absence of references/endpoints, then
removed only that named volume and network. The success result was read. Final inspection
found zero managed runner containers, transient CI containers/networks or new volumes
since deployment; the pinned orchestrator remained running with zero restarts.

## Host network outage - 2026-11-19

`gh-runner-2` could not resolve `api.github.com` from 02:04 to 08:03 UTC (`Resource
temporarily unavailable` on every repository each cycle); the VM itself stayed up. Nothing
was restarted. At 08:03 both installations took a new token, the first cycles served the
backlog (a sandbox job and five saas-foundry jobs queued during the outage) and no runner
or container was left behind. Each renewal also warned that revoking the replaced token
failed with 401: those tokens had expired during the outage. Fixed in
[#43](https://github.com/thiagoluga/gh-runners-orchestrator/pull/43). Deployed on
2026-11-19 at 09:28 UTC after Thiago's approval: Compose pins
`sha256:60ac89315a8d11eab3f1743aef457f8c8ef1127d7191631416acd3ab2aed9357` (revision
`2e28e5c`), backup `compose.yaml.before-2e28e5c` for rollback. Both installations renewed,
no revoke warning, and a sandbox smoke (run 44496916274) passed with no container left.

## Two Aspire applications in two slots - 2026-11-19

[#35](https://github.com/thiagoluga/gh-runners-orchestrator/issues/35). The
`aspire-isolation` workflow of `thiagoluga/runner-pool-sandbox` runs one minimal Aspire
13.6.5 AppHost (one PostgreSQL container, as saas-foundry) in each slot at once and checks
every 5 s that its own container is still running, with DCP debug logs kept as artifacts.

| Run | Result |
|---|---|
| 44487348063 | leg 2's container never appeared in 2 minutes; leg 1 ran its full 180 s |
| 44488048224 | leg 1's container was killed and destroyed 5 s after it started; leg 2 ran its full time |
| 44488565004 | leg 2's container was destroyed 6 s after it started; leg 1 ran its full time |
| 44489094609 | leg 1 lost again; leg 2 ran its full time |

In every run exactly one application lost its containers, and it was the one started
first, a few seconds after the other started. Its own DCP did it: the API server logged
`API server changed status Running -> Stopping` from its admin HTTP handler, then
`BeforeApiSrvShutdown ... ResourceCleanup: Full`, and deleted the session's containers and
network, while its AppHost had not asked to stop (no shutdown line in the AppHost log). The
two runner containers share only the host network namespace and the Docker daemon, so the
stop request or its trigger crosses between them through one of those; which one was not
isolated. Each leg alone, and saas-foundry at `MaxConcurrent: 1`, run green.

The rule is in [operations.md](operations.md#enable-or-disable-a-repository): never two
Aspire applications at once on one host. Today only saas-foundry starts Aspire AppHosts;
insurapp does not.

## Status endpoint deployed - 2026-11-19

[#22](https://github.com/thiagoluga/gh-runners-orchestrator/issues/22) /
[#45](https://github.com/thiagoluga/gh-runners-orchestrator/pull/45), revision `736ed64`,
image digest `sha256:9c82290e7f6845bd11ea1603f8bec37f93fc95dabdda622588cb29708b5bf174`, on
gh-runner-2 at 13:55 UTC. The host's `compose.yaml` gained the loopback port mapping and
`Orchestrator__StatusPort`; the previous file is `compose.yaml.before-736ed64`.

- The log showed `Status endpoint listening on port 9464`; both installations renewed their
  tokens and cycles ran normally.
- `curl http://127.0.0.1:9464/healthz` answered `ok` (200); `/metrics` listed every
  repository and both accounts' budgets. The same port on the host's LAN address refused
  the connection.
- Sandbox smoke run 44538472789 passed; afterwards `ghro_runners_launched_total 1`,
  `ghro_runners_finished_total 1`, `ghro_launch_failures_total 0`.
