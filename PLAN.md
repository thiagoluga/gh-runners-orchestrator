# Plan

Ordered work. Mirrors the [roadmap issue](https://github.com/thiagoluga/gh-runners-orchestrator/issues/1);
each issue is the source of truth for its scope. One open pull request at a time, grouped
per phase ([ADR 0007](docs/adr/0007-branching-and-delivery.md)).

Plan agreed with Thiago (2026-11-15): the design in [docs/architecture.md](docs/architecture.md),
ADRs 0001–0008, and the four delivery PRs below.

| PR | Phase | Issues | Status |
|---|---|---|---|
| 1 | [P1 Design and repository setup](https://github.com/thiagoluga/gh-runners-orchestrator/issues/2) | [#7](https://github.com/thiagoluga/gh-runners-orchestrator/issues/7) | done |
| — | maintainer step | [#8](https://github.com/thiagoluga/gh-runners-orchestrator/issues/8) GitHub App, Sonar project, toy repository | done |
| 2 | [P2 Proof of concept and CI baseline](https://github.com/thiagoluga/gh-runners-orchestrator/issues/3) | [#9](https://github.com/thiagoluga/gh-runners-orchestrator/issues/9), [#10](https://github.com/thiagoluga/gh-runners-orchestrator/issues/10), [#11](https://github.com/thiagoluga/gh-runners-orchestrator/issues/11), [#12](https://github.com/thiagoluga/gh-runners-orchestrator/issues/12) | done |
| 3 | [P3 Runner pool](https://github.com/thiagoluga/gh-runners-orchestrator/issues/4) | [#13](https://github.com/thiagoluga/gh-runners-orchestrator/issues/13), [#14](https://github.com/thiagoluga/gh-runners-orchestrator/issues/14), [#15](https://github.com/thiagoluga/gh-runners-orchestrator/issues/15), [#16](https://github.com/thiagoluga/gh-runners-orchestrator/issues/16), [#17](https://github.com/thiagoluga/gh-runners-orchestrator/issues/17) | in review |
| 4 | [P4 saas-foundry migration](https://github.com/thiagoluga/gh-runners-orchestrator/issues/5) | [#18](https://github.com/thiagoluga/gh-runners-orchestrator/issues/18); then [#19](https://github.com/thiagoluga/gh-runners-orchestrator/issues/19) after confirmation | not started |
| — | [P5 Deferred](https://github.com/thiagoluga/gh-runners-orchestrator/issues/6) | [#20](https://github.com/thiagoluga/gh-runners-orchestrator/issues/20), [#21](https://github.com/thiagoluga/gh-runners-orchestrator/issues/21), [#22](https://github.com/thiagoluga/gh-runners-orchestrator/issues/22) | deferred, not authorized |

## Constraints every phase keeps

- The current runner `vm-dev-1` (saas-foundry) keeps working until the pool has proved the
  same CI green: foundation, generator, generated-project, Sonar.
- The pool registers runners with label `vm-pool` until [#19](https://github.com/thiagoluga/gh-runners-orchestrator/issues/19).
- Measured facts from saas-foundry (`docs/ci-runners.md` there): `nofile` 65536, real Docker
  Engine, `cpuset` not quota, 14 GB peak, about 12 minutes per pull request, uplink about
  90 KB/s. Images are built by CI and pulled by the host, never pushed from it.
- A registration token or JIT configuration never reaches a shell command or a log. Files
  passed from Windows are cleaned with `tr -d '[:space:]'` (a trailing CR broke
  `config.sh` once) and deleted afterwards.
- Nothing is called working until it ran for real and its result was read.

## Technical risks

| Risk | Where it is checked |
|---|---|
| A token scoped to `administration:write` cannot call `generate-jitconfig` on a user repository | [#12](https://github.com/thiagoluga/gh-runners-orchestrator/issues/12) |
| The runner ignores the JIT configuration from the environment | [#12](https://github.com/thiagoluga/gh-runners-orchestrator/issues/12) |
| Labels on queued jobs differ from `runs-on` | [#12](https://github.com/thiagoluga/gh-runners-orchestrator/issues/12) |
| Two slots are too slow for saas-foundry's three heavy jobs | [#18](https://github.com/thiagoluga/gh-runners-orchestrator/issues/18) |
| Sibling containers leak after a killed runner | [#15](https://github.com/thiagoluga/gh-runners-orchestrator/issues/15) |
