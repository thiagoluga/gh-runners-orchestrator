# 0012. Register multiple GitHub Apps in one pool

- Status: Accepted
- Date: 2026-11-16
- Deciders: Thiago Lugarini
- Supersedes: the single-App restriction in ADR 0011.

## Context

A private GitHub App can only be installed in its owning account. The maintainer wants
separate private Apps for trusted accounts without making the original App public.
The pool already isolates installation tokens, caches and rate budgets by owner.

## Decision

Add Orchestrator.GitHubApps, a list of AppId and host-only PrivateKeyPath entries with no
hardcoded count limit. Each Accounts entry explicitly selects its AppId and InstallationId.
App IDs and installation IDs are positive and unique; owners remain unique. One account
uses one registered App installation, and an App may serve multiple authorized accounts.
All Apps use the same GitHub API base URL; separate GitHub servers are outside this change.

Retain GitHubApp + Owner and GitHubApp + Accounts configuration compatibility. Accounts
may omit AppId only in single-App mode; a supplied ID must match. GitHubApps and a configured
legacy GitHubApp cannot be mixed. Registry mode requires explicit Accounts and AppId links.
Validate every registration and reference before startup. Unused registrations are allowed;
keys are loaded only for accounts that use them. Registration changes require a restart.

Select the App before constructing each account credential source. Sign JWTs with that
App's ID/key and validate returned App, owner and installation metadata. Never try another
App/key if signing, authentication or renewal fails. Treat missing, malformed and non-private
keys as a failure of only that account; never echo key content or cryptographic errors.

Keep one global coordinator/planner and existing allowlist/concurrency limits. New containers
also carry App ID; cleanup rejects an inconsistent App label. Existing installation-labelled
containers remain compatible because installation IDs are globally unique. Unlabelled legacy
containers still require the drain procedure of ADR 0011. Changing App/installation binding
with outstanding runners requires draining first; ambiguous resources reserve capacity.

## Consequences

Each account can keep its App private and grant only Actions read, Administration write
and Metadata read. The maintainer creates/installs Apps and provisions each key on the host;
configuration contains paths only. Compose must mount each selected key read-only into the
orchestrator, never into runners. Supply a standalone multi-App deployment example and retain
the original single-App deployment. No App creation, visibility change, host operation,
consumer change, new dependency or deployment is part of this implementation PR.

Keys still live on a Docker-socket-accessible host: this adds credentials to that existing
trust boundary, not job isolation. Removing an App requires draining its accounts first.
Rollback restores the previous config, mounts and image after new-account runners drain.
