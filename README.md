# gh-runners-orchestrator

One pool of self-hosted GitHub Actions capacity for several repositories of
**explicitly authorized personal accounts**, with **one fresh container per job**.

> Status: pool implemented; multi-account activation pending. See [PLAN.md](PLAN.md) and the
> [roadmap](https://github.com/thiagoluga/gh-runners-orchestrator/issues/1).

## Why

A self-hosted runner registered on a personal account serves exactly one repository. With
several private projects, that means one idle runner per project, each holding cores and
memory, or jobs that never get a runner.

The orchestrator watches the repositories you enable. When a job asks for the pool's
label, it creates a [just-in-time runner](https://docs.github.com/en/rest/actions/self-hosted-runners#create-configuration-for-a-just-in-time-runner-for-a-repository)
for that repository in a new container. The container runs that one job and is removed.
Capacity is a fixed set of slots with pinned cores and a memory limit; jobs beyond it wait
in GitHub's queue.

## How it works

See [docs/architecture.md](docs/architecture.md). Decisions and their reasons are in
[docs/adr/](docs/adr/README.md).

## Read this before enabling a repository

Every runner container shares the host's Docker socket and network. **Any job can see and
control every other job on the host**, including other repositories' jobs and their
secrets. Enable only **private repositories you own**, on a host that holds nothing else.
Never use self-hosted runners for public repositories: a fork's pull request would run its
code on your host. The orchestrator refuses public repositories and unauthorized owners, but the
rule matters more than the check. Details: [ADR 0006](docs/adr/0006-security-model.md) and
[ADR 0011](docs/adr/0011-multiple-account-installations.md).

## Requirements

- Linux host with **Docker Engine** (not Docker Desktop: it publishes container ports only
  on the Windows side, which breaks Testcontainers and Aspire inside jobs).
- A GitHub App you own, installed on the enabled repositories, with Actions read and
  Administration read and write ([ADR 0002](docs/adr/0002-github-app-credential.md)).

## Setup

1. Create the GitHub App, install it on the repositories to serve and copy its key to the
   host: [docs/maintainer-setup.md](docs/maintainer-setup.md).
2. Install and run the pool on the host: [docs/operations.md](docs/operations.md).
3. Add more runner hosts: [docs/new-runner-host.md](docs/new-runner-host.md).

## Contributing

Multi-account configuration and activation gates: [docs/multi-account.md](docs/multi-account.md).

See [CONTRIBUTING.md](CONTRIBUTING.md). Security reports: [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE).
