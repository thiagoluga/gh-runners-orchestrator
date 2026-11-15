# 0007. Deliver from `main` through pull requests, a few per phase

- Status: Accepted
- Date: 2026-11-15
- Deciders: Thiago Lugarini

## Context

The maintainer's runbook defaults to a permanent `test` branch (staging) with task PRs
into it and deliberate promotion to `main`, and to documentation committed directly to
`test`. That model exists for products with a staging environment.

This project is a tool. There is no staging environment: a version is verified on the
host by running real jobs, then tagged.

The maintainer also asked for coarser pull requests on this project, to move faster.

## Decision

- **Single long-lived branch `main`.** Every change, documentation included, arrives by a
  pull request from a short-lived branch (`docs/…`, `feat/…`, `fix/…`, `ci/…`).
- `main` is protected by a ruleset: no deletion, no force push, pull request required,
  review threads resolved, squash merge only. Required status checks are added with the
  first workflow.
- **One open pull request at a time.** The maintainer merges.
- **Pull requests group a phase's tasks** (one PR may close several task issues) instead
  of one PR per task. Each PR lists the issues it closes and what it does not do yet.
- Conventional Commits for commits and PR titles; the squash commit takes the PR title.
- Releases are tags `v*` on `main`, after the version ran real jobs on the host.

## Alternatives considered

- **Runbook default (`test` + `main`).** Adds a promotion step with nothing to promote to.
- **One PR per task.** Rejected by the maintainer for this project as too granular.

## Consequences

- Larger PRs need a clear description and a checklist per closed issue, or review gets
  shallow.
- Documentation changes wait for a merge like code does. Acceptable: there is one
  maintainer and one open PR at a time.
