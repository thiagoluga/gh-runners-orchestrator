# Proof of concept report

[#12](https://github.com/thiagoluga/gh-runners-orchestrator/issues/12), run on 2026-11-15
from the code of the pull request that closes it.

## Setup

| Item | Value |
|---|---|
| Host | `gh-runner-2`: Ubuntu 24.04 on Hyper-V, 16 vCPUs, 40 GiB, Docker Engine 29.9.3, cgroup v2 |
| Images | built **on the host** from the branch (the host only downloads); CI publishing to GHCR starts after the merge |
| Orchestrator | `docker run` of the orchestrator image, App key mounted read-only, socket group added |
| Slots | `slot-1` cores `0-4` 16 GB, `slot-2` cores `5-9` 16 GB |
| Repository | `thiagoluga/runner-pool-sandbox` (private), App installed only there |

## Results

Every scenario of the sandbox repository, dispatched by hand:

| Scenario | Result | Measured |
|---|---|---|
| `smoke` | green | dispatch → container 17 s; job 7 s; container removed 6 s after the job; runner gone from the API |
| (in `smoke`) slot limits | as configured | `cpuset.cpus.effective` `0-4` (5 cores), `memory.max` 17179869184, `nofile` 65536 |
| (in `smoke`) JIT configuration | not in the job environment | the runner reads `ACTIONS_RUNNER_INPUT_JITCONFIG` and the step saw it empty |
| `sibling-container` | green | PostgreSQL started through the host Docker reachable on `localhost` after 2 s |
| `memory-limit` | green | asking for 20 GB in a 16 GB slot ended with exit code 137 (OOM kill) |
| `capacity` (3 legs, 2 slots) | green | at most 2 legs at once; the third started 11 s after a slot freed |
| `cancel` | green | cancelled after 20 s of the job; container removed 12 s after the cancel; runner gone |

Across the 14 minutes of the run (42 cycles, 8 runners): API budget used **52 requests**
of 5,000 (idle cycles answered `304` and cost nothing), orchestrator memory **39 MiB**,
CPU about 0%. No container, volume or runner registration was left behind
(`docker ps -a`, `docker volume ls`, `GET /actions/runners` all empty). The orchestrator
log contains no token, key or JIT configuration (searched for `ghs_`, `BEGIN`, `jitconfig`,
`eyJ`).

## Open questions from the ADRs, now answered

| Question | Answer |
|---|---|
| Can a token scoped down to `administration:write` call `generate-jitconfig` on a user repository? (ADR 0002) | Yes. |
| Does the runner honor the JIT configuration from the environment? (ADR 0004) | Yes, and the job does not inherit the variable. |
| Do queued jobs carry the labels `runs-on` asked for? (ADR 0001) | Yes: `runs-on: vm-pool` gave `["vm-pool"]`. |
| Does `ETag`/`304` work for an installation token on these list endpoints? (ADR 0001) | Yes: idle cycles did not lower the remaining budget. |

## Not verified here

- Publishing the images to GHCR and pulling them on the host (runs on the first push to
  `main`; the package visibility may need the maintainer).
- Idle-runner timeout, orphan sweep, configuration reload, restart and host reboot: phase 3.
- A job can still read its own runner's JIT configuration through `docker inspect` or
  `/proc` (same user, Docker socket). It is single-use and already consumed by then; the
  risk is inside the same-owner model of ADR 0006.
- Heavy workloads (saas-foundry's CI): phase 4.

## Found on the way

- The account has **no GitHub-hosted minutes for private repositories**: a job on
  `ubuntu-24.04` in the sandbox was refused at once ("recent account payments have failed
  or your spending limit needs to be increased"). Every job of a private repository has to
  run on the pool. This public repository's CI is not affected.
