# 0013. Run one peer orchestrator per host, sharing the pool label

- Status: Accepted
- Date: 2026-11-28
- Deciders: Thiago Lugarini
- Defers: [0009](0009-central-controller-and-host-agents.md) (stays Proposed for a later
  web interface or more hosts); supersedes its point 6 (one label per host).

## Context

The maintainer has a second runner VM, `github-runner` (192.168.68.50, 8 vCPUs, 30 GiB),
next to `gh-runner-2` (192.168.68.66, 16 vCPUs, 40 GiB), and wants both to serve the same
repositories under the same `vm-pool` label.

What breaks if two copies of today's orchestrator simply run on the two hosts:

- **Duplicate runners.** Both see the same queued jobs and each launches a runner for
  them; the extra runners sit idle until the idle timeout (10 minutes) and hold slots.
- **Cross-host deletion.** The orphan sweep deletes every offline, idle `orch-` runner that
  has no container on the local Docker. A runner the other host has just registered is
  offline for a few seconds before its container connects, so each host would delete the
  other's new runners.
- **Per-repository `MaxConcurrent` would become per host**, doubling it (saas-foundry
  must stay at one Aspire job per host, [#35](https://github.com/thiagoluga/gh-runners-orchestrator/issues/35)).

ADR 0009 solves all three with a controller on the production VPS and outbound agents. That
moves the App key to the VPS, needs a TLS transport, agent authentication and a VPS
deployment, and makes every new runner depend on the VPS being up. Two hosts on the same
LAN do not need that yet.

GitHub's runner list is already a shared view: every registration of every host is listed
by name, with its status (`online`/`offline`) and whether it is busy.

## Decision

1. **One orchestrator per host, as today, each owning only its host's slots and Docker.**
   No orchestrator talks to another host's Docker or to another orchestrator. Each host
   keeps the App key file of its own; a host that is down stops only its own slots.
2. **Host identity.** `Orchestrator:Host` names this host and `Orchestrator:PoolHosts`
   lists every host of the pool, in the same order on every host. Both empty is today's
   single-host mode, with unchanged behavior and runner names. Host names are short,
   lower-case, distinct, and never contain `--`.
3. **Runner names carry the host**: `orch-<host>--<repository>-<slot>-<random>`. GitHub
   account names cannot contain `--`, so a name is never mistaken for a single-host name
   (`orch-<owner>-<repository>-…`). Containers also carry an `orchestrator.host` label.
4. **Each host cleans up only its own runners.** The orphan-registration sweep deletes an
   offline runner only if its name carries this host. Single-host names belong to the first
   entry of `PoolHosts`, so runners left from before the switch are still cleaned up.
   Runners of a host not in `PoolHosts` are left alone.
5. **Each queued job has an owner host**: `PoolHosts[job id mod N]`. In a cycle a host
   counts, per repository, the runners that wait for a job (its own not-busy containers
   plus the other hosts' online, not-busy runners), treats the oldest queued jobs as
   covered by them, and launches only for uncovered jobs that it owns or that have waited
   longer than `Orchestrator:TakeOverAfter` (default 1 minute, at least two poll
   intervals). So a job reaches a free slot of any host within that delay when its owner is
   full or down.
6. **`MaxConcurrent` is global.** A host counts the other hosts' online runners of the
   repository with its own containers. Offline foreign registrations are not counted, so a
   crashed host's leftovers never block the others.
7. Fairness ([0005](0005-capacity-slots-and-fairness.md)) stays per host, over the same
   global running counts.

## Alternatives considered

- **Controller and agents ([0009](0009-central-controller-and-host-agents.md)).** The
  right shape for many hosts or a web interface; not chosen now for the cost and exposure
  in its Consequences. It stays Proposed.
- **One orchestrator driving both Docker Engines** (remote Docker over TLS or an SSH
  tunnel). One planner, no coordination, but the host every job can take over would hold
  root-equivalent access to the other host's Docker, joining two blast radii into one.
- **One label per host.** No coordination needed, but repositories would have to choose a
  host in their workflows and could not use free capacity on the other.
- **Coordination through a lock or lease** (a file share, a database, a GitHub variable).
  Adds a store both hosts must reach and a failure mode of its own; GitHub's runner list
  already gives each host the view it needs.

## Consequences

- Easier: adding a host is one more orchestrator with its own slots and the same lists;
  losing a host only loses its slots after `TakeOverAfter`.
- **Each host polls GitHub.** API use per installation roughly multiplies by the number of
  hosts; idle cycles stay almost free thanks to `ETag`/`304`. Watch
  `ghro_rate_limit_remaining` on each host.
- **Duplicates are rare, not impossible.** Two hosts can both launch for one job when its
  owner frees a slot in the same cycle in which the other host's takeover delay expires, or
  in the seconds before a foreign runner comes online. The extra runner is removed by the
  idle timeout. Watch `ghro_runners_launched_total` against jobs run.
- A job may wait up to `TakeOverAfter` longer than with one host when its owner is full
  and another host is free.
- Changing `PoolHosts` changes job ownership; do it on every host, then restart them.
  Removing a host leaves its registrations to it; drain it first.
- Hosts share nothing but GitHub: the Aspire rule (#35) now holds per host, so two Aspire
  jobs on different hosts do not interfere. `MaxConcurrent` stays global, so saas-foundry at
  1 still runs one job at a time across the pool.
- Not verified yet at the time of writing: measured on both VMs in the pull request that
  introduces this ADR ([pool-verification.md](../pool-verification.md)).
