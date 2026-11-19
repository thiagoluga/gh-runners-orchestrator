# 0003. Write it as a .NET 10 Worker Service with almost no dependencies

- Status: Accepted
- Date: 2026-11-15
- Deciders: Thiago Lugarini

## Context

The orchestrator is a long-running loop: read configuration, poll an HTTP API, call
another HTTP API over a Unix socket (Docker Engine), keep a small amount of state, log.
No database, no user interface, no inbound HTTP in the first version.

The maintainer works in .NET 10, and the shared runbook standardizes .NET for products
(xUnit v3, AwesomeAssertions, NSubstitute, Central Package Management, analyzers as
errors, Sonar).

## Decision

- **.NET 10 Worker Service** (`Microsoft.NET.Sdk.Worker`, Generic Host, `BackgroundService`),
  published as a container image ([0008](0008-portable-deployment.md)).
- **GitHub**: a small client over `HttpClient` and `System.Text.Json` source generation.
  The JWT is signed with `System.Security.Cryptography` (RS256). No Octokit: six endpoints
  do not justify a large, reflection-heavy dependency.
- **Docker**: a small client for the Docker Engine HTTP API over the Unix socket
  (`SocketsHttpHandler.ConnectCallback` to `/var/run/docker.sock`). Endpoints used: create,
  start, wait/inspect, remove, list by label, and the events or polling needed to notice an
  exit. No Docker.DotNet: its upstream is barely maintained and the surface we need is tiny.
- Both clients sit behind interfaces (`IGitHubRunnerApi`, `IContainerRuntime`), so the
  queue and capacity logic is tested without network or Docker.
- **Time**: `TimeProvider`, faked in tests with `FakeTimeProvider`. This deviates from the
  runbook's NodaTime default: there are no calendar dates, time zones or user-facing times
  here, only timeouts and intervals, and `TimeProvider` is in the base library.
- **Configuration**: JSON (`appsettings.json` plus a repository list file) through
  `Microsoft.Extensions.Configuration`, with `reloadOnChange`. No YAML parser.
- **No Aspire, no PostgreSQL.** This deviates from the runbook's product platform. There is
  nothing for Aspire to orchestrate locally, and the state lives in Docker labels
  ([0004](0004-ephemeral-runner-lifecycle.md)). Adding either would be ceremony.
- No `IHttpClientFactory` package: each client owns one long-lived `HttpClient` with a
  `SocketsHttpHandler` (`PooledConnectionLifetime`), which is what the factory would give.

### Approved packages (Thiago, 2026-11-15, batch)

| Package | Use |
|---|---|
| `Microsoft.Extensions.Hosting` | Generic Host, configuration, logging (comes with the Worker SDK) |
| `xunit.v3` | test framework |
| `AwesomeAssertions` | assertions (Apache-2.0 fork of FluentAssertions) |
| `NSubstitute` | substitutes for the GitHub and Docker interfaces |
| `Microsoft.Extensions.TimeProvider.Testing` | `FakeTimeProvider` |
| `coverlet.collector` | coverage for Sonar |
| `Microsoft.NET.Test.Sdk` | test host |
| `xunit.runner.visualstudio` | Approved by Thiago on 2026-11-15. xUnit's own VSTest adapter: `dotnet test` with `coverlet.collector` finds no tests without it (same as saas-foundry). |
| `dotnet-sonarscanner` (tool) | Sonar analysis in CI |

Versions and actual licenses (including transitive ones) are checked when each package
is added. Anything outside this list needs a new approval.

## Alternatives considered

- **Go.** Small static binary, first-class Docker SDK. Not chosen: the maintainer reads and
  reviews .NET, and the binary size does not matter on a VM with 40 GB of memory.
- **An existing tool** (`actions-runner-controller`, `philips-labs/terraform-aws-github-runner`).
  The first needs Kubernetes, the second AWS; both are out of scope.
- **A shell script with `gh` and `docker`.** Fine for the proof, but capacity, fairness,
  reload and failure handling would be untestable.

## Consequences

- One runtime dependency family, owned by Microsoft.
- Our Docker client must be tested against a real Engine before it is trusted: fakes prove
  the scheduler, not the protocol. The proof of concept is that test.
