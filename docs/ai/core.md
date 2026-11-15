# Core rules for AI agents

Loaded by `CLAUDE.md` and `AGENTS.md`. Short on purpose; open the indexed files only when
the task needs them.

## Product boundary

An orchestrator of ephemeral GitHub Actions runners: one container per job, a fixed pool
of slots shared by the maintainer's **private** repositories on one Linux Docker Engine
host. Out of scope until an ADR says otherwise: web UI, Kubernetes, several hosts, public
or third-party repositories, autoscaling of hosts.

## Non-negotiable

1. **Everything committed is in English.** Only the chat with Thiago is in Brazilian
   Portuguese.
2. **No secret anywhere but the host**: App private key, installation tokens, JWTs, JIT
   configurations and registration tokens never go into the repository, a log, a label, an
   exception message, a command line or the chat. A token passed from Windows is written
   to a file with `umask 077`, read with `tr -d '[:space:]'`, and the file is deleted.
3. **Nothing is "working" until it ran for real** and the result line was read. Report
   what was measured, what was not verified, and what needs Thiago's decision.
4. **This repository's CI runs only on GitHub-hosted runners.** It is public.
5. **The current runner `vm-dev-1` stays untouched** until [#19](https://github.com/thiagoluga/gh-runners-orchestrator/issues/19).
6. **Confirm before destructive actions** on the host (removing containers, volumes,
   runner registrations not created by the orchestrator, `docker system prune`).
7. Capacity uses `cpuset` and memory limits, **never a CPU quota** (measured to throttle).

## How to work

- Branch from `main`, PR to `main`, one open PR at a time, PRs grouped per phase; merge
  when every required check is green and every thread is resolved. Documentation-only
  changes go straight to `main`, without a PR; CI ignores them
  ([ADR 0007](../adr/0007-branching-and-delivery.md)).
- Conventional Commits. Author Thiago, committer Claude:
  `GIT_COMMITTER_NAME=Claude GIT_COMMITTER_EMAIL=noreply@anthropic.com git commit`
  with the repo-local identity `Thiago Lugarini <thiagoluga@hotmail.com>`. Trailer
  `Co-Authored-By: Claude <model> <noreply@anthropic.com>`. Never `--no-verify`.
- Before a code commit: build, tests and format green, `/code-review` and
  `/security-review` on the diff, fix what is real.
- After push: CI green, Sonar at zero new issues, CodeQL clean, every review thread
  answered and resolved.
- Packages: only the approved list in [ADR 0003](../adr/0003-stack-dotnet-worker-service.md).
  Anything else needs Thiago's approval first.
- Cite issues and PRs as clickable links.

## The host (first deployment)

The pool runs on its own VM, `gh-runner-2`: Ubuntu 24.04 on Hyper-V, 16 vCPUs, 40 GiB of fixed memory,
static `192.168.68.66/22`, Docker Engine 29 (`nofile` 65536 by default, cgroup v2), user
`ci` with passwordless sudo, SSH by key only:
`ssh -i ~/.ssh/ci_vm_ed25519 ci@192.168.68.66`. The old VM `192.168.68.50` runs `vm-dev-1`
and is not touched (rule 5). From Windows, use SSH from PowerShell;
send scripts by `scp` and run them with `bash file < /dev/null` (piped scripts get CRLF and
`docker exec -i` swallows stdin). Do not hammer the SSH port with polling.

## Index

| Topic | File |
|---|---|
| Architecture, components, configuration shape | [docs/architecture.md](../architecture.md) |
| Decisions | [docs/adr/](../adr/README.md) |
| Ordered work and risks | [PLAN.md](../../PLAN.md) |
| Security model | [ADR 0006](../adr/0006-security-model.md) |
| Maintainer steps (App, Sonar, toy repository) | [docs/maintainer-setup.md](../maintainer-setup.md) |
| Proof of concept: measured results | [docs/proof-of-concept.md](../proof-of-concept.md) |
| Operations (install, enable, update, rotate) | [docs/operations.md](../operations.md) |
| Runner pool verification | [docs/pool-verification.md](../pool-verification.md) |
| Contribution rules | [CONTRIBUTING.md](../../CONTRIBUTING.md) |

## Operational lessons

Numbered, dated, never rewritten.

1. (2026-11-15, inherited from saas-foundry) `nofile` 1024 kept Aspire hosts from
   starting; 65536 fixed it.
2. (2026-11-15, inherited) A CPU quota throttled .NET builds until jobs timed out; `cpuset`
   fixed it.
3. (2026-11-15, inherited) Three heavy jobs on the same cores broke the generator's
   10-minute limit.
4. (2026-11-15, inherited) A token piped from PowerShell carried a CR and `config.sh`
   failed with "New-line characters are not allowed in header values".
5. (2026-11-15, inherited) Uploading 12 MB of artifacts from the VM stalled for 27 minutes
   (uplink about 90 KB/s). Build images in CI and pull them on the host.
6. (2026-11-15) The account has no GitHub-hosted minutes for private repositories: a job on
   `ubuntu-24.04` in the sandbox is refused at once. Private repositories run every job on
   the pool; this public repository's CI is unaffected.
