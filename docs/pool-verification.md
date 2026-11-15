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

## Not verified here

- **Two repositories sharing the slots** ([#13](https://github.com/thiagoluga/gh-runners-orchestrator/issues/13)):
  needs a second private repository with the App installed, which is a maintainer step. The
  rule is covered by unit tests (`CapacityPlannerTests`).
- **Fresh install from `main` and host reboot** ([#16](https://github.com/thiagoluga/gh-runners-orchestrator/issues/16)):
  needs the published orchestrator image of this phase, so it runs after the merge.
- **Runner version workflow** ([#17](https://github.com/thiagoluga/gh-runners-orchestrator/issues/17)):
  runs from `main` only; checked after the merge against a deliberately old pin.

## Observed

Each runner start lowered the installation's API budget by about 35 requests in the next
cycle, while the orchestrator itself makes about 5. The rest is most likely the runner's own
calls, counted on the same installation; not investigated further. At 5,000 per hour this
allows well over 100 runner starts per hour.
