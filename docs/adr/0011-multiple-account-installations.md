# 0011. Route repository access through explicit account installations

- Status: Accepted
- Date: 2026-11-16
- Deciders: Thiago Lugarini
- Supersedes: the single-owner restriction of ADRs 0002 and 0006.

## Context

The maintainer requests service for private repositories in thiagoluga and maozinha,
without transferring insurapp or changing the existing pool capacity. Both accounts
are trusted by the maintainer; jobs still share a root-equivalent Docker socket and
host network. This does not introduce isolation between accounts or repositories.

## Decision

Keep one PoolCycle and one global slot planner. Add an optional Orchestrator.Accounts
list of Owner and InstallationId pairs for the same GitHub App. In this mode Owner
must be absent; owners and positive installation IDs must be unique. With no Accounts,
the existing Owner configuration discovers its installation as before. Repository
allowlisting remains the reloadable qualified Repositories list.

Route every repository request to its owner's API client and credential source. Each
has independent token renewal/revocation, ETag cache, failure backoff and rate budget.
Validate installation account, App ID and configured installation ID against GitHub.
Validate installed repository identities and private visibility before issuing the
pool token, and enforce installation membership before repository API requests.
Never fall back to another account. Revalidate installation metadata on renewal and
invalidate a failed credential/cache so revocation cannot reuse cached authorization.

New containers carry their installation ID. Cleanup uses qualified repository identity
and checks the installation label against the configured/discovered installation.
Legacy unlabelled containers are accepted only for the legacy Owner; drain them before
switching to Accounts. Ambiguous containers retain their slots and are left untouched.
A failed account must not stop demand or cleanup for a healthy account. The existing
fewest-running/oldest-job policy applies across all qualified repositories.

No App permission expansion is required. App installation availability must be checked
separately: the original setup selected Only on this account. The maintainer installs
the App on maozinha/insurapp and approves deployment/restart after reviewing the final
image, non-secret configuration diff, commands, impact and rollback. This PR does not
activate either a new account or a consumer workflow.

## Consequences

Existing configuration and single-owner behavior remain supported. Accounts changes
need a restart; repository-list changes remain reloadable. Removing a repository stops
new launches while its installation can still clean existing registrations. Removing
an account or changing its installation intentionally leaves its resources untouched
and capacity reserved until the maintainer resolves them. Roll back to legacy mode
only after draining new-account runners; never let an older binary sweep their labels.
Cross-project Aspire/port/container conflicts remain unresolved (issue 35); per-repo
MaxConcurrent=1 does not prevent interference between repositories.
