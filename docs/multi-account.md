# Multiple trusted accounts

[ADR 0011](adr/0011-multiple-account-installations.md) extends the existing pool, not its
capacity or isolation. One process and one planner control the two global slots. Account
names, repository names and installation IDs are not credentials. The App key remains a
host-only file; never open it to discover metadata.

## Configuration and compatibility

The existing `Orchestrator.Owner` configuration continues to work unchanged, including
installation discovery, qualified repository names and reloadable repository limits.
For explicit account mode, **remove Owner** and add:

```jsonc
"Accounts": [
  { "Owner": "thiagoluga", "InstallationId": 0 },
  { "Owner": "maozinha", "InstallationId": 0 }
]
```

The zeros above are intentionally invalid placeholders, not installation IDs. Replace
them with the verified installation IDs of App **5770327** for each account before using
this configuration. Both owners and positive IDs must be unique. Setting both `Owner`
and `Accounts`, malformed owners, duplicate owners/IDs or nonpositive IDs fails startup.
`Accounts` is host configuration: changing it requires a restart. Environment overrides
must not leave a legacy `Orchestrator__Owner` when selecting explicit accounts.

`Repositories` retains its existing shape and reload behavior. Names must be qualified;
duplicate names (including case variants), malformed names and negative caps are refused.
An unauthorized, public, transferred, renamed or installation-uncovered repository gets
no new runner. GitHub metadata must agree on repository, owner, App and installation.
No installation token can be used as fallback for another account.

## Observed host configuration (2026-11-16, read-only)

Non-secret configuration at `~/gh-runners-orchestrator/config/` on `gh-runner-2`:

- `orchestrator.json`: `Owner=thiagoluga`, App ID `5770327`, runner image
  `ghcr.io/thiagoluga/gh-runners-orchestrator-runner:2.338.2`, labels `vm-pool` and `vm-dev`,
  polling every 20 seconds, idle timeout 10 minutes.
- Global slots: `slot-1` cores `0-4`, `slot-2` cores `5-9`, each `16g`; no CPU quota.
- `repositories.json`: `thiagoluga/runner-pool-sandbox` and
  `thiagoluga/runner-pool-sandbox-2` at `MaxConcurrent: 2`, and
  `thiagoluga/saas-foundry` at `MaxConcurrent: 1`.
- Running orchestrator reference: `ghcr.io/thiagoluga/gh-runners-orchestrator:main`;
  image ID `sha256:bc8ddf9447e3fd6487fec682fdc268c390e8add88a2c2bafb7df271008b990fd`.
  An image ID is a local image identity, not a registry manifest digest.

The checked-in runner Dockerfile includes git, curl, jq, zip/unzip, gh, pwsh, Docker CLI,
Compose, Buildx and Java 21. The runner user has no sudo grant. .NET/Node/Python SDKs need
setup per job; there is no persistent tool cache. Tools were not executed inside the
deployed runner image, so this is not a new runtime verification of its contents.

## App installation gate

Both owners are personal accounts (GitHub metadata checked 2026-11-16). The original
[App setup](maintainer-setup.md) selected **Only on this account**. A public metadata
lookup of `thiagoluga-runner-pool` returned 404, and the available CLI credential could
not list App installations (403). These responses do not confirm current App visibility,
permissions, installation IDs or whether maozinha can install it.

Before activation, the maintainer must inspect GitHub > Settings > Developer settings >
GitHub Apps > `thiagoluga-runner-pool` and confirm the App ID is **5770327**, its current
installation availability, and permissions: Actions read, Administration write and
Metadata read, with no organization/account permissions or events. If availability must
change to permit another account, present that concrete setting change for approval;
this implementation does not change it or require wider repository permissions.

After availability is confirmed, the maintainer installs the same App in `maozinha`,
selecting **only the private insurapp repository**, and confirms the installation
metadata. Do not substitute the App ID for an installation ID. Verify each installation's
account and App ID; request-time checks also reject a mismatch. Exact installation
values and a usable install link remain pending this metadata verification.

## Proposed non-secret activation diff

No files on the host were changed by this PR. Preserve every existing field and repository:

```diff
-    "Owner": "thiagoluga",
+    "Accounts": [
+      { "Owner": "thiagoluga", "InstallationId": <verified-thiagoluga-installation-id> },
+      { "Owner": "maozinha", "InstallationId": <verified-maozinha-installation-id> }
+    ],
```

Then append to `Repositories`, preserving the three existing entries:

```json
{ "Name": "maozinha/insurapp", "MaxConcurrent": 1 }
```

The angle-bracket values are review placeholders, not valid JSON. A final valid diff,
immutable orchestrator image reference built from the merged change, exact commands and
backup paths must be presented together for approval before any host write or restart.
The runner image, labels, limits and saas-foundry cap remain unchanged.

## Activation and rollback sequence (not executed)

1. Verify App availability, permissions and both installations. Do not change consumer
   CI variables yet. Record the deployed registry image reference and digest for rollback;
   `main` is mutable and the local image ID alone is not a pull target.
2. With deployment approval, first install the new orchestrator using the **legacy Owner
   configuration**, leaving repositories and capacity unchanged. Verify existing thiagoluga
   jobs and cycle logs. This stage supports rollback to the previous binary/configuration.
3. Before switching to `Accounts`, wait for existing **unlabelled legacy runners** to finish
   and be reaped; do not stop their jobs. New runners carry `orchestrator.installation-id`.
   Pause new demand through an approved, reversible repository-list edit if draining needs
   it; document the impact on saas-foundry before doing so.
4. After approval of the final diff, back up non-secret configuration and the image pin,
   select the reviewed orchestrator image, apply explicit accounts, then restart the
   orchestrator only. Rebuild state from runner labels; never duplicate the coordinator.
5. Confirm thiagoluga service, each installation identity, rejection paths, two-account
   capacity, cleanup and restart on private sandbox targets. Unit tests do not establish
   live API compatibility or host isolation. insurapp onboarding is a separate task.
6. To disable maozinha, remove its repository from the list first and let running jobs
   finish. Keep the account and App installation until its runners/registrations are gone.
   Then restore the backed-up legacy configuration and restart as separately approved.
   Only after that may the older binary be restored; never run it against new-account
   containers, because it does not understand installation labels. Restore the original
   repositories file and image pin, then verify thiagoluga. Do not remove App access before
   cleanup or remove an account while it still holds resources.

A missing/mismatched installation label in explicit mode, or a removed account, leaves the
container and its slot reserved for operator resolution. Old unlabelled containers are
accepted only in legacy Owner mode. Cleanup uses qualified repository plus runner name/ID;
identical names or numeric runner IDs in another account do not identify the same resource.
Repository removal stops launches but installed private repositories remain visible to the
orphan sweep after a process restart. Revoked/unavailable installations are left untouched.

## Verification and limits

Automated tests use generated keys and fake tokens/HTTP, Docker and time boundaries. They
cover routing, homonyms, renewals, removal/revocation, invalid metadata/configuration,
independent rate budgets, global capacity, restarts and cleanup correlation. The existing
single-owner tests continue to run. Record actual test/check results in the delivery PR.

Real two-account JIT execution, deployed-image contents, host restart and cleanup acceptance
remain pending installation, deployment approval and private sandbox verification using
[the existing scenarios](pool-verification.md). No new real-host run is claimed here.

Jobs still share Docker and host networking, including readable job secrets. Account
separation in the API is not job isolation. `MaxConcurrent: 1` per repository does not stop
cross-repository interference; investigate ports, names, volumes and global paths before
insurapp jobs run beside saas-foundry ([issue 35](https://github.com/thiagoluga/gh-runners-orchestrator/issues/35)).

No consumer workflow, CI_RUNNER, upload requirement or rollback policy is changed. No new
GitHub spend is enabled. Pool execution does not remove artifact storage quotas. External
private evidence/package storage requires a separate task with byte, count and retention
limits; do not weaken mandatory uploads to make CI appear green.
