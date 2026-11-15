# 0002. Authenticate as a GitHub App installed only on enabled repositories

- Status: Accepted
- Date: 2026-11-15
- Deciders: Thiago Lugarini

## Context

The orchestrator calls two kinds of endpoint on each enabled repository:

| Endpoint | Repository permission |
|---|---|
| list workflow runs and jobs | Actions: read |
| `POST /repos/{o}/{r}/actions/runners/generate-jitconfig`, list and delete runners | Administration: read and write |
| repository metadata (to check it is private) | Metadata: read (always granted) |

Repository-level self-hosted runners require **Administration: write**. GitHub has no
narrower permission for them. That permission also lets the holder change repository
settings, so whoever holds the credential holds a lot.

The repositories belong to a personal account (`thiagoluga`), which has no organization
runner groups.

## Decision

- Use a **GitHub App** owned by the personal account, installed **only on the repositories
  that are enabled**, with exactly the three permissions above and no events.
- The orchestrator signs a short-lived JWT (RS256, 9 minutes) with the App's private key,
  finds the installation, and requests an installation token **scoped down** to the
  repositories it serves and to `actions:read` + `administration:write`
  (`POST /app/installations/{id}/access_tokens` with `repositories` and `permissions`).
  The token lives in memory and is renewed before its one-hour expiry.
- The private key lives only on the host that runs the orchestrator: a file readable only
  by the orchestrator (mode `0400`), mounted read-only into its container. It never goes
  into the repository, a manifest, a log, an environment variable printed anywhere, or a
  chat.
- Creating the App and its key is the maintainer's step. Documentation gives the exact
  value of each field; the agent checks only metadata afterwards (permissions, installed
  repositories).

## Alternatives considered

- **Fine-grained personal access token.** Same `Administration: write` scope, but tied to
  the user, with a maximum lifetime that forces a manual rotation, and with no way to ask
  for a narrower token per request. Kept only as a last resort, behind the same interface
  (`IGitHubCredentialSource`), if the App route turns out to be blocked.
- **Classic PAT with `repo` scope.** Far broader than needed. Rejected.

## Consequences

- Enabling a repository is two steps: add it to the App installation (GitHub settings) and
  to the orchestrator's list. The orchestrator refuses, and logs, a listed repository that
  the installation does not cover.
- A leaked key gives repository administration over every repository the App is installed
  on. Keeping the installation list equal to the enabled list limits that.
- Key rotation: generate a new key in the App settings, replace the file, restart; then
  delete the old key in GitHub.
- Not verified yet: that a token scoped down to `administration:write` can call
  `generate-jitconfig` on a user-owned repository (proof of concept).
