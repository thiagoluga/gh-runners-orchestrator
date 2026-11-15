# AGENTS.md

Read [docs/ai/core.md](docs/ai/core.md) first; it holds the rules for every task and an
index of the rest.

Agents without review commands: make two explicit passes over the full pending diff
(correctness, then security: secrets, logs, Docker socket exposure) before committing.
