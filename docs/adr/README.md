# Architecture decision records

A decision recorded here is not reopened without a new ADR that supersedes it. Copy
[0000-template.md](0000-template.md) for a new one.

| # | Decision | Status |
|---|---|---|
| [0001](0001-detect-queued-jobs-by-polling.md) | Detect queued jobs by polling the REST API; webhook deferred | Accepted |
| [0002](0002-github-app-credential.md) | GitHub App installed only on enabled repositories; PAT as last resort | Accepted |
| [0003](0003-stack-dotnet-worker-service.md) | .NET 10 Worker Service, own small GitHub and Docker clients, approved packages | Accepted |
| [0004](0004-ephemeral-runner-lifecycle.md) | One JIT runner per container, one job per runner, Docker labels as state | Accepted |
| [0005](0005-capacity-slots-and-fairness.md) | Fixed slots with `cpuset` and memory limits, fewest-running-first fairness | Accepted |
| [0006](0006-security-model.md) | Only the maintainer's private repositories, on a host that holds nothing else | Accepted |
| [0007](0007-branching-and-delivery.md) | `main` only, pull requests for everything, grouped per phase | Accepted |
| [0008](0008-portable-deployment.md) | Container image for any Linux Docker Engine host | Accepted |
