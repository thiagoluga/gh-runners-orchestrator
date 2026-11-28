# Changelog

Notable changes, newest first. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) from the first release.

## Unreleased

### Fixed

- Detect queued runner jobs inside workflows marked `pending` by job concurrency, while
  continuing to ignore jobs still blocked on concurrency or requiring other labels.

### Added

- Several runner hosts: one peer orchestrator per host shares the pool label. Each queued
  job has an owner host and the others take it over after `TakeOverAfter`; runner names
  carry the host and each host cleans up only its own; `MaxConcurrent` is global
  ([ADR 0013](docs/adr/0013-peer-orchestrators-on-several-hosts.md)).

- Optional status endpoint: `/healthz` and Prometheus `/metrics` with cycle, slot,
  demand, launch and rate-budget counters, published on the host's loopback by the
  reference deployment ([#22](https://github.com/thiagoluga/gh-runners-orchestrator/issues/22)).

- Configurable GitHub App registry with explicit per-account App bindings and independent
  keys, preserving single-App configurations and global capacity. Includes private-App
  deployment examples and safe handling of local key failures.

- Optional explicit account/installation configuration sharing the same global capacity;
  legacy single-owner configuration remains supported. Tokens, caches, rate limits and
  failures are isolated by installation; cleanup validates qualified identity and labels.
  Activation, migration and rollback are documented in `docs/multi-account.md`.

- Design: architecture, ADRs 0001–0008, plan and roadmap.
- Repository files: contributing guide, code of conduct, security policy, issue forms and
  pull request template.
