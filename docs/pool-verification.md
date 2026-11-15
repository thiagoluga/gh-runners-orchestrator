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

## Not verified

- **Two repositories sharing the slots** ([#13](https://github.com/thiagoluga/gh-runners-orchestrator/issues/13)):
  needs a second private repository with the App installed, a maintainer step. The rule is
  covered by unit tests (`CapacityPlannerTests`).
- **The version workflow opening its pull request**: needs the repository setting
  *Settings → Actions → General → Allow GitHub Actions to create and approve pull requests*,
  a maintainer decision.

## Observed

Each runner start lowered the installation's API budget by about 35 requests in the next
cycle, while the orchestrator itself makes about 5. The rest is most likely the runner's own
calls, counted on the same installation; not investigated further. At 5,000 per hour this
allows well over 100 runner starts per hour.
