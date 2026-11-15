# 0008. Ship the orchestrator as a container for any Linux Docker Engine host

- Status: Accepted
- Date: 2026-11-15
- Deciders: Thiago Lugarini

## Context

The first host is an Ubuntu 24.04 Server VM on Hyper-V (8 vCPUs today, about 12 vCPUs and
40 GB soon, Docker Engine, static address, SSH by key only). The maintainer stated that the
orchestrator runs on that VM today but must be able to run in any environment.

What the jobs need from the host is not negotiable and was measured: a **real Docker
Engine** on Linux. Docker Desktop publishes random host ports only on the Windows side, so
Testcontainers and Aspire cannot reach them from a runner container.

## Decision

- The orchestrator is published as a **container image** (multi-stage build, .NET 10
  runtime, non-root user that belongs to the socket's group).
- It runs on **any Linux host with Docker Engine**: a VM, a bare-metal machine, a cloud
  instance. A `compose.yaml` example is the reference deployment.
- Nothing in the code assumes the VM: the Docker socket path, slot cores and memory, labels,
  intervals, runner image and paths are configuration. The VM's values live in an example
  file, not in defaults.
- The App private key is a file on the host mounted read-only (a Compose `secrets:` entry),
  never an environment variable.
- The runner image is versioned in this repository (moved from saas-foundry's
  `eng/runners/`), with every downloaded binary checked against a pinned SHA-256.

## Alternatives considered

- **systemd service with a self-contained binary.** Simpler key custody on one VM, but ties
  the deployment to systemd hosts. It remains possible (the binary is the same), just not
  the documented path.
- **Kubernetes.** Out of scope.

## Consequences

- The orchestrator container itself mounts the Docker socket: it is root-equivalent on the
  host, like every runner ([0006](0006-security-model.md)).
- `restart: unless-stopped` brings it back after a Docker or host restart; the orphan sweep
  then cleans what the restart interrupted. Behavior after a host reboot is verified before
  the migration is called done.
- Planned evolution: a central controller on the production VPS with agents on the runner
  hosts ([0009](0009-central-controller-and-host-agents.md), proposed). It moves the App key
  off the runner host; this ADR still governs the runner hosts themselves.
