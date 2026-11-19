# CLAUDE.md

Read [docs/ai/core.md](docs/ai/core.md) first; it holds the rules for every task and an
index of the rest.

Claude-specific:

- Use the repository-local author `Thiago Lugarini <thiagoluga@hotmail.com>`. Set
  `GIT_COMMITTER_NAME=Claude` and `GIT_COMMITTER_EMAIL=noreply@anthropic.com` for the
  commit process only, with trailer `Co-Authored-By: Claude <model> <noreply@anthropic.com>`
  using the actual model name. Never change the global Git identity.
- Run `/code-review` and `/security-review` through the Skill tool before every code commit
  and wait for the result; if one cannot run, say that gate did not run.
- Chat with Thiago in Brazilian Portuguese; everything written to the repository in English.
- For host (VM) work, use SSH from PowerShell as described in the core file.
