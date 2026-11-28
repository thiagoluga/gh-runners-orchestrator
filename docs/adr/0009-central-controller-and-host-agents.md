# 0009. Run a central controller on the production VPS and an agent on each runner host

- Status: Proposed (planned after the saas-foundry migration, phase 4); deferred by
  [0013](0013-peer-orchestrators-on-several-hosts.md), which replaces point 6
- Date: 2026-11-15
- Deciders: Thiago Lugarini

## Context

Today one orchestrator runs on the runner host and drives its local Docker Engine
([0004](0004-ephemeral-runner-lifecycle.md), [0008](0008-portable-deployment.md)). The
maintainer wants more than one runner host, the option to install without containers on a
VM, and possibly a web interface. All of it fits the current design if planning moves away
from the runner host.

Facts:

- The orchestrator is light: 39 MiB of memory, about 0% CPU, no disk state; idle cycles
  cost no API requests ([proof of concept](../proof-of-concept.md)). Planning, capacity,
  lifecycle and the repository guard are pure logic over GitHub's API and a list of
  containers.
- The runner hosts are on a home network with no public address; the production VPS
  (Dokploy, which runs the maintainer's products) has one, behind Traefik and Cloudflare.
- Every runner host exposes its Docker socket to every job, so it is root-equivalent to any
  job ([0006](0006-security-model.md)).
- Existing open-source managers (GARM, RunnerRunner) use the same split of a controller and
  host agents. They were not adopted: GARM's Docker provider does not apply `cpuset`,
  `nofile` or host networking and needs scale sets or webhooks; RunnerRunner needs webhooks
  for on-demand runners, applies no CPU or memory limits and has no license.

## Decision

1. **Controller** on the production VPS, deployed as its own Dokploy project:
   - holds the GitHub App key, polls GitHub, runs the repository guard, the capacity planner
     and the lifecycle sweep across the slots of every host;
   - runs as a non-root container with a memory limit and **without access to the VPS's
     Docker socket**; it never starts a container on the VPS;
   - is reachable only through Traefik over TLS: the agent endpoint and, later, the web
     interface. Nothing else is exposed.
2. **Agent** on each runner host (container or systemd service, same binary):
   - connects **outbound** to the controller over TLS, so no port opens on the home network;
   - reports its slots and its managed containers, and executes create, start, list and
     remove on its local Docker Engine. It never holds the App key or an installation token;
   - authenticates with a token of its own, valid only for that host. The controller treats
     agent reports as untrusted input: an agent can only act on, and report about, its own
     slots.
3. A JIT configuration travels from the controller to one agent only for the runner that
   agent is about to create. It is single-use, never logged, and dropped by the agent once
   the container exists.
4. The Docker labels on each host stay the source of truth for that host's runners; the
   controller rebuilds the pool from the agents' reports after a restart, as today.
5. `IContainerRuntime` becomes the boundary between controller and agent. A host backend
   other than Docker (a VM per job, for example) is another implementation of it.
6. Until a second runner host exists, the current single-process deployment stays. Several
   hosts can run one orchestrator each meanwhile, each with its own label, as long as two
   orchestrators never serve the same label.
7. With a public endpoint available, the `workflow_job` webhook
   ([#20](https://github.com/thiagoluga/gh-runners-orchestrator/issues/20)) may be added to
   the controller to start runners at once; polling stays as the safety net
   ([0001](0001-detect-queued-jobs-by-polling.md)).

## Alternatives considered

- **Controller on one of the runner hosts.** No production exposure, but the host every job
  can take over through the Docker socket would hold the App key and command every other
  host. Rejected.
- **A separate small VPS for the controller.** Cleanest isolation, but one more machine to
  pay for and maintain. Kept as the fallback if sharing the production VPS becomes a
  concern.
- **One orchestrator per host, no controller.** Works with one label per host; with a shared
  label both see the same queue and over-create runners until the idle timeout removes them.
  Accepted only as the interim step (point 6).
- **Adopt GARM or RunnerRunner.** See Context. Revisit if maintaining this becomes more
  expensive than adapting one of them.

## Consequences

- **The App key moves to the production VPS.** Whoever takes over the VPS also gets
  repository administration on every repository the App is installed on, and the reverse.
  Accepted because the VPS already holds credentials of the same weight (deploys,
  databases); written down here so the trade-off is explicit. Key custody follows
  [0002](0002-github-app-credential.md): a file secret, mode 0400, never in an environment
  variable, a log or the chat.
- **The controller commands root-equivalent hosts.** Its endpoint and tokens become
  high-value; the web interface, when it comes, needs authentication before anything else
  and should not be reachable outside Cloudflare's protection.
- **Availability:** if the VPS is down or in maintenance, no new runner starts on any host;
  jobs already running finish. Documented in the operations guide.
- The planner, lifecycle and guard code move unchanged; the transport, agent
  authentication, reconnection and per-host capacity are new code and need their own tests
  and a verification like phase 3's.
- Not verified yet: agent reconnection after a network drop, the controller restarting
  while agents hold running containers, and latency from the home network to the VPS.
