# Security policy

## Reporting a vulnerability

Use GitHub's private advisory form:
[github.com/thiagoluga/gh-runners-orchestrator/security/advisories/new](https://github.com/thiagoluga/gh-runners-orchestrator/security/advisories/new).

Please do not open a public issue for a vulnerability. The orchestrator holds a credential
with repository administration rights and controls the host's Docker Engine, so a public
report is a working exploit against every host that has not updated.

You will get a first reply within 7 days. There is no bounty.

## What matters most here

- **Anything that leaks a credential**: the App private key, an installation token, a JWT,
  or a JIT configuration reaching a log, a container label, an exception, an image layer or
  an API response.
- **Anything that lets a job outlive its container** or leave files, credentials or
  processes for the next job.
- **Anything that makes the orchestrator serve a repository it should refuse**: public,
  owned by someone else, or not in the enabled list.
- **Anything that lets a repository take more capacity than it was given**, or starve the
  others.

## Known and accepted, not a finding

- **Jobs share the host's Docker socket and network.** Any job can control every other
  container on the host. This is why only the maintainer's private repositories are
  enabled, on a host that holds nothing else. See
  [ADR 0006](docs/adr/0006-security-model.md).
- **The orchestrator itself mounts the Docker socket.** It is root-equivalent on the host by
  design.

## Supported versions

Only the latest release. There is no long-term support branch.
