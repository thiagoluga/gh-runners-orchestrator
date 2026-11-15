# 0006. Serve only the maintainer's private repositories, on a host that holds nothing else

- Status: Accepted
- Date: 2026-11-15
- Deciders: Thiago Lugarini

## Context

Every runner container mounts the host's Docker socket and uses the host network, because
the jobs start PostgreSQL and Mailpit through Docker and reach them on `localhost`. Access
to the Docker socket is root-equivalent on the host.

So every job, from every enabled repository, can:

- list, inspect, stop or remove any container on the host, including other repositories'
  runners and the orchestrator itself;
- read another running job's environment and files through `docker inspect` and
  `docker exec`;
- read the orchestrator's private key by starting a container that mounts the host path.

Self-hosted runners on a **public** repository are worse: a pull request from a fork runs
attacker code on the host.

## Decision

1. **Only private repositories owned by the maintainer are enabled.** The orchestrator
   checks this itself, at start and on every reload: a listed repository that is public,
   or whose owner is not the configured owner, is refused and logged. The check uses the
   API, not the configuration.
2. **The host runs nothing else of value.** No other project's containers, no production
   data, no credentials beyond the App key.
3. **The App credential** follows [0002](0002-github-app-credential.md): key only on the host,
   `0400`, mounted read-only; installation tokens scoped down per request; nothing secret in
   logs.
4. **Nothing secret is logged.** JIT configurations, installation tokens, JWTs and the key
   are never written to a log, an exception message or a container label. The logging
   layer has a test that fails if a known secret shape (`ghs_`, `-----BEGIN`, a JIT
   configuration) reaches it.
5. **Each container is destroyed at the end of its job**, with its volumes
   ([0004](0004-ephemeral-runner-lifecycle.md)). No state carries from one job to the next
   inside the runner.
6. **This repository is public and its own CI never runs on self-hosted runners.** Its
   workflows use GitHub-hosted runners only.

## Alternatives considered

- **Docker-in-Docker per job** (`--privileged` daemon inside the runner). Isolates the
  containers jobs start from each other, but `--privileged` is itself root on the host,
  and Testcontainers/Aspire need extra setup to reach ports inside the nested daemon.
  Rejected for now; revisit if a repository that is not the maintainer's ever needs the
  pool.
- **Rootless Docker or user namespaces.** Not tried. Kept as a possible hardening issue.

## Consequences

- The risk between repositories is accepted because they all belong to the same person.
  This document is the reason that acceptance is written down.
- Secrets of a running job (`GITHUB_TOKEN`, `SONAR_TOKEN`) are readable by any other job
  running at the same time on the host. Repositories that hold production deploy keys
  should not be enabled, or should deploy from GitHub-hosted runners.
- Planned evolution: a central controller on the production VPS with agents on the runner
  hosts ([0009](0009-central-controller-and-host-agents.md), proposed). It moves the App key
  off the runner host; this ADR still governs the runner hosts themselves.
