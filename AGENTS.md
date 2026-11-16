# AGENTS.md

Read [docs/ai/core.md](docs/ai/core.md) first; it holds the rules for every task and an
index of the rest. These shared rules also apply to Claude through `CLAUDE.md`.

## Codex

- Chat with Thiago in Brazilian Portuguese; everything committed is in English.
- Use the repository-local author `Thiago Lugarini <thiagoluga@hotmail.com>`.
  For commits made by Codex, set `GIT_COMMITTER_NAME=Codex` and
  `GIT_COMMITTER_EMAIL=noreply@openai.com` for the commit process only. Include
  `Co-Authored-By: Codex <noreply@openai.com>`. Do not change the global Git identity.
- Codex does not require Claude's Skill tool or slash commands. Before every commit,
  make two explicit passes over the **full pending diff**:
  1. Correctness: behavior, configuration compatibility, failure paths, tests and docs.
  2. Security: secrets, logs, permissions, untrusted inputs and Docker socket exposure.
  Fix real findings; record the results and checks that could not run in the PR.
  These passes also apply to other agents without review commands.
- Before code or workflow commits, run the restore, formatting, build and test commands
  from [the CI workflow](.github/workflows/ci.yml). Do not claim remote CI, Sonar or
  CodeQL passed until their results have been read.
- For host work, use SSH from PowerShell as described in the core file. Host operations
  still require the approvals defined there and in the current user request.
- Follow the requested scope and stopping point. If Thiago reserves the merge or asks
  to wait for it before another task, deliver the PR and wait; do not merge or deploy.
