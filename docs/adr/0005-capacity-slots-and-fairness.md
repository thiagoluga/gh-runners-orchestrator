# 0005. Fixed slots with pinned cores and a memory limit, shared fairly

- Status: Accepted
- Date: 2026-11-15
- Deciders: Thiago Lugarini

## Context

Measured on the current runner (saas-foundry):

- A CPU **quota** (`cpus:`) throttled .NET builds (18% to 20% of the periods, 22 minutes in
  one runner) and jobs timed out. Pinning cores with `cpuset` fixed it.
- Three heavy jobs of one pull request on the **same** set of cores made workers lose
  communication and exceeded the generator's own 10-minute limit.
- The heaviest job (foundation) peaks at **14 GB**. A full pull request takes about
  12 minutes on 8 vCPUs.

The VM will have about 12 vCPUs and 40 GB.

## Decision

- Capacity is a list of **slots** in configuration. Each slot has its own `cpuset` and a
  memory limit. A slot holds at most one runner container. Slots never share cores.
- First layout for 12 vCPUs / 40 GB:

  | Slot | Cores | Memory |
  |---|---|---|
  | `slot-1` | `0-4` | 16 GB |
  | `slot-2` | `5-9` | 16 GB |
  | host and sibling containers | `10-11` | about 8 GB |

  Sibling containers started by jobs (PostgreSQL, Mailpit) are outside the slot limits;
  the spare cores and memory are for them and for the host. Before fixing this layout, the
  14 GB peak is re-measured on the VM to know how much of it is the runner container and
  how much the siblings.
- A slot is taken from the moment a runner is created, not when it becomes busy: a JIT
  runner waiting for its job still holds its slot.
- **Fairness.** When a slot is free, it goes to the repository with demand that has the
  **fewest running containers**; ties go to the repository whose oldest queued job has
  waited longest. Each repository may also have a `MaxConcurrent` cap.
- Jobs beyond capacity simply wait in GitHub's queue. The orchestrator keeps no queue of
  its own.
- The scheduler is a pure function of (slots, running containers, demand per repository,
  now) → actions. That is what the unit tests exercise.

## Alternatives considered

- **CPU quota per container** (`--cpus`): measured to throttle. Rejected.
- **No limits, as many containers as jobs**: measured to break the generator's time limit.
  Rejected.
- **Dynamic slot sizes** (a bigger slot for the foundation job): GitHub does not tell us in
  advance which job a JIT runner will get. Rejected for now; a second label (`vm-pool-large`)
  mapped to a dedicated slot can come later if a measurement asks for it.

## Consequences

- With two slots, a pull request with three heavy jobs runs two at once and the third
  waits. The measured full-pipeline time on the pool is part of the migration report.
- Changing the layout is a configuration change and a restart; the scheduler never moves a
  running container.
