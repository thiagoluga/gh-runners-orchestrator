# 0004. One just-in-time runner per container, one job per runner

- Status: Accepted
- Date: 2026-11-15
- Deciders: Thiago Lugarini

## Context

A personal-account runner serves one repository. Keeping a registered, idle runner per
repository wastes memory and cores and does not scale past a few projects. GitHub's
just-in-time (JIT) configuration creates a runner that is already registered, takes
exactly one job and unregisters itself afterwards.

Facts measured on the current runner (saas-foundry, `docs/ci-runners.md` there) that the
container must keep: `ulimit nofile` 65536, host networking, the host Docker socket, a
real Docker Engine.

## Decision

### Creating a runner

1. The scheduler ([0005](0005-capacity-slots-and-fairness.md)) grants a slot to a repository
   with demand.
2. `POST /repos/{o}/{r}/actions/runners/generate-jitconfig` with a unique name
   (`orch-<repo>-<slot>-<8 random chars>`), the pool labels, `runner_group_id: 1` and
   `work_folder: _work`.
3. Create the container from the pinned runner image with:
   - labels `orchestrator.managed=true`, `orchestrator.repo`, `orchestrator.slot`,
     `orchestrator.runner-name`, `orchestrator.runner-id`, `orchestrator.created-at`;
   - the slot's `cpuset` and memory limit (memory and swap equal), no CPU quota;
   - `nofile` 65536, host network, the Docker socket, the socket's group id;
   - the JIT configuration in an environment variable read by the entrypoint, which
     removes it from the environment before the job starts. Never on the orchestrator's
     command line, never in a log.
4. Start it.

### While it lives

- The **Docker labels are the state.** On start, and on every cycle, the orchestrator
  rebuilds its view from `docker ps -a --filter label=orchestrator.managed=true`. A restart
  of the orchestrator loses nothing and duplicates nothing.
- A runner that has not picked up a job after `IdleRunnerTimeout` (default 10 min) is
  removed: `DELETE` the runner on GitHub, then remove the container. This covers a job
  cancelled before pickup and a job taken by another runner.
- GitHub assigns the runner to **any** queued job of that repository whose labels match.
  The orchestrator never binds a runner to a job id; it only keeps
  `runners not yet busy <= matching queued jobs` per repository.

### Ending

- The runner exits after its job. The orchestrator records the exit code and the duration,
  then removes the container **with its anonymous volumes** (`docker rm -v`). The work
  folder lives inside the container, so nothing of the job survives.
- Containers are not created with auto-remove: the exit code must be read first, and a
  sweep removes any exited managed container left behind by a crash.
- **Orphan sweep** (every cycle): a managed container whose runner no longer exists on
  GitHub is removed; a runner on GitHub with the `orch-` prefix, offline, with no container,
  is deleted.

## Alternatives considered

- **Registration token + `config.sh --ephemeral`.** Same one-job behavior, but the
  registration token is reusable for an hour and has to reach the container. JIT
  configuration is single-use and already bound to one runner name.
- **Persistent runners per repository, started on demand.** Keeps state between jobs, which
  is what leaks one job's files and credentials into the next.

## Consequences

- No tool cache between jobs: `setup-dotnet` downloads the SDK every job. The VM's download
  is fast (about 45 MB/s), so this is accepted; a shared read-only tool cache is a deferred
  issue.
- Containers a job starts **through the host Docker** (PostgreSQL, Mailpit, Testcontainers)
  are siblings, not children: removing the runner does not remove them, and their memory is
  not inside the slot limit. Testcontainers' reaper cleans up after a normal exit; after a
  killed runner they can leak. The orchestrator does **not** remove containers it did not
  create, because it cannot tell a leaked one from something else on the host. This is
  documented in the operations guide with the command to find them.
- Not verified yet: that the runner honors the environment variable for the JIT
  configuration (`ACTIONS_RUNNER_INPUT_JITCONFIG`), and the exact labels GitHub adds to a
  JIT runner (`self-hosted`, OS, architecture). Both are checked in the proof of concept.
