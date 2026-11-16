# 0010. Support Codex and Claude contribution workflows

- Status: Accepted
- Date: 2026-11-16
- Deciders: Thiago Lugarini
- Supersedes: the agent-specific merge assumption and documentation-PR restriction in
  [ADR 0007](0007-branching-and-delivery.md). Other delivery rules remain unchanged.

## Context

The maintainer requested Codex compatibility in a separate PR before multi-account work.
Shared instructions assumed Claude attribution and Claude-only review commands.
Documentation PRs could not complete required checks because workflows ignored them.

## Decision

At the maintainer's request, Codex and Claude share the rules in `docs/ai/core.md`,
with agent-specific identity and review instructions in `AGENTS.md` and `CLAUDE.md`.
Codex uses two explicit full-diff review passes instead of Claude-only review commands.

Documentation still defaults to direct pushes. An explicit maintainer request for a
PR overrides that default. CI, CodeQL and Images now run on every PR to `main`, including
documentation PRs, so required checks can complete. Documentation-only pushes to `main`
remain ignored. This supersedes the documentation-PR restriction in the previous amendment.

The maintainer requested this compatibility PR separately while an automated dependency
PR was already open. Preserve that PR; this is a one-time exception to the one-open-PR
rule. The maintainer will merge the compatibility PR before multi-account work begins.

The active agent may merge only when authorized by the maintainer. Keep agent-specific
commit attribution in each entry point; keep product and security rules shared.

## Consequences

Both agents follow the same safety constraints without requiring each other's tools.
Documentation PRs use GitHub-hosted CI time; this public repository's runner policy and
workflow permissions remain unchanged. Runtime, host configuration and dependencies
are unaffected. No multi-account implementation or deployment belongs in this change.
