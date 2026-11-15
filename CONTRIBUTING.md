# Contributing

## Everything committed is in English

Code, identifiers, comments, commit messages, docs, log messages, test names, ADRs.
Discussion in issues and pull requests can be in any language.

## Read the decisions first

The design is in [docs/architecture.md](docs/architecture.md) and the reasons are in
[docs/adr/](docs/adr/README.md). A pull request that contradicts an accepted ADR needs a
new ADR that supersedes it, in the same pull request.

## Verify against a real host

The orchestrator talks to two things a unit test cannot reproduce faithfully: GitHub's API
and a Docker Engine. The queue and capacity logic is tested with fakes; the clients are not
trusted until they ran against the real thing. A pull request that changes either client
says what it ran on a real host and what it printed.

Use a Linux host with Docker Engine. Docker Desktop behaves differently for the
containers jobs start (published ports exist only on the Windows side) and hides exactly
the problems this project exists to handle.

## Never paste a secret

No App private key, installation token, JIT configuration or registration token in an
issue, a pull request, a log excerpt or a test fixture. Redact before sharing. Tests use
generated keys and fake tokens.

## How changes arrive

- Branch from `main`; open a pull request to `main`. Squash merge; the PR title becomes the
  commit, so it follows [Conventional Commits](https://www.conventionalcommits.org/)
  (`feat(scheduler): ...`, `fix(docker): ...`, `docs: ...`).
- CI, Sonar and CodeQL must be green, and every review thread answered and resolved.
- New dependencies need the maintainer's approval first; the current list is in
  [ADR 0003](docs/adr/0003-stack-dotnet-worker-service.md).
- Update [CHANGELOG.md](CHANGELOG.md) when an operator would notice the change.

## Questions and ideas

Use [Discussions](https://github.com/thiagoluga/gh-runners-orchestrator/discussions).
Issues are for bugs and agreed work.
