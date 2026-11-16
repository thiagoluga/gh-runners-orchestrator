# Multiple private GitHub Apps

[ADR 0012](adr/0012-multiple-github-apps.md) lets the pool use any configured number of
GitHub Apps without making them public. Keep `thiagoluga-runner-pool` private. Create a
separate private App owned by maozinha, installed only on its private insurapp repository.
App creation, key provisioning, installation and deployment remain maintainer steps.
The implementation PR performed none of them. The approved reference-host activation is
recorded below; Insurapp source and CI_RUNNER were not modified.

## Shape and compatibility

Legacy configurations remain valid without migration:

- `Owner` plus `GitHubApp`: one owner, installation discovered as before.
- `Accounts` plus `GitHubApp`: multiple explicit installations of one App. Account AppId
  may be omitted; if supplied, it must match the single configured App.

For the new registry, omit `Owner` and `GitHubApp`, then use:

```jsonc
"GitHubApps": [
  { "AppId": 5770327, "PrivateKeyPath": "/run/secrets/github-apps/thiagoluga.pem" },
  { "AppId": 0, "PrivateKeyPath": "/run/secrets/github-apps/maozinha.pem" }
],
"Accounts": [
  { "Owner": "thiagoluga", "AppId": 5770327, "InstallationId": 0 },
  { "Owner": "maozinha", "AppId": 0, "InstallationId": 0 }
]
```

**Every zero is an intentionally invalid placeholder** requiring verified metadata.
App ID and installation ID are different numbers. Verified reference-host IDs are recorded
below; the zeros remain example placeholders.
Do not deploy the example unchanged.
[The complete configuration example](../deploy/config/orchestrator.apps.example.json)
retains the two slots, image and labels. Preserve the actual repository list, including
saas-foundry at MaxConcurrent=1; append insurapp at 1 only after its installation is verified.

There is no hardcoded App count limit. Each account references one registered App and
installation; an App may serve multiple accounts. Owners, App IDs and installation IDs
must be unique in their respective lists, IDs positive, paths non-empty, and all references
resolvable. Multiple Apps on the same owner are not supported: owner-based routing must
remain unambiguous. Unused App registrations are allowed and their keys are not loaded.
All registered Apps use the same absolute HTTPS ApiBaseUrl (default https://api.github.com/).
Different GitHub servers are outside this deployment model.

Mixing a configured legacy GitHubApp with GitHubApps fails startup. Remove legacy
`Orchestrator__GitHubApp__*` environment overrides when using the registry. Registry and
account changes require a restart; Repositories remains independently reloadable.

## Key files and Compose

Only key **paths** belong in JSON. Private key contents, JWTs, tokens and JIT configurations
never belong in environment configuration, command arguments, logs, repository or artifacts.
Each account signs with its selected App's key and validates App ID, owner and installation
metadata. Failure never tries another App or key. Token/cache/rate-budget state remains
separate per installation. Missing, invalid and public-only PEM files fail that account
without echoing their contents or stopping healthy accounts.

The original [compose.yaml](../deploy/compose.yaml) remains the single-App deployment.
The standalone [compose.apps.example.yaml](../deploy/compose.apps.example.yaml) replaces it
for registry mode; it is **not a second orchestrator service** and is not an additive override.
It uses the same Compose project/service names and mounts a host-only directory read-only
at `/run/secrets/github-apps`. It does not mount keys into runners. Set `GITHUB_APPS_KEY_DIR`
to the reviewed absolute directory on the host; `create_host_path: false` refuses an absent
source rather than creating one accidentally. Keep that directory limited to the selected
App keys, owned by UID 1654 and mode 0700; key files mode 0400, UID 1654.

For example, after approval and host-only provisioning, configuration could refer to
`/run/secrets/github-apps/thiagoluga.pem` and `/run/secrets/github-apps/maozinha.pem`.
Adding another App then needs its key file, registration and account mapping, rather than
another Compose service or a change to a hardcoded secret list. Do not place keys under
`config/` or copy them into the checkout. Existing single-App key provisioning is described
in [maintainer-setup.md](maintainer-setup.md); never read a real key to inspect metadata.

This directory mount and the existing Docker socket are inside the same trust boundary:
trusted jobs can control the host daemon and read host files. Multiple Apps do not isolate
jobs or their credentials. Per-repository concurrency caps do not solve cross-project
port/container/volume conflicts (issue 35).

## Approval, migration and rollback

The implementation PR performed no host changes. For the approved activation, see the
reference-host record below. The previously observed configuration and restrictions
in [multi-account.md](multi-account.md) still apply, with separate private Apps replacing
the previously proposed public/multi-installation App. Publishing an App is unnecessary.

Before activation, verify for each owner the App ID, installation ID, private visibility,
selected private repositories and existing permissions: Actions read, Administration write,
Metadata read, no organization/account permissions or subscriptions. No permission expansion
is required. Provide a complete non-secret JSON/Compose diff, exact commands, immutable
orchestrator image reference, backup paths, impact and rollback for explicit approval.

1. Preserve current labels, slots, image and the three thiagoluga repositories. Provision
   keys only through the approved host-only procedure. Existing App/key remain valid.
2. Install the new binary with legacy configuration first and validate thiagoluga service.
   Drain old unlabelled runners normally before switching to explicit account mode.
3. With approval, back up JSON, Compose, environment pins and the previous registry image
   digest; replace the deployment with the reviewed standalone multi-App Compose file,
   set the key-directory path and apply the verified registry/account mappings.
4. Recreate only the orchestrator service with the selected image, preserving the single
   project/service identity. Existing runners retain their slots. Installation-labelled
   runners created before App labels were introduced remain supported, since installation
   IDs are globally unique; present App labels must match the selected binding.
5. Confirm real thiagoluga and new-account JIT/runner operation, two-slot capacity, renewal,
   restart and cleanup on approved private targets. These checks remain pending approval
   and the new App installation; passing automated tests does not establish live acceptance.
6. Before removing/changing an App binding, disable its repositories and let its runners
   finish and registrations be cleaned while the old binding/key remain available. Then
   restore the backed-up config, Compose/environment and image as approved. Do not run an
   older binary against another account's outstanding runners or delete key files early.

Wrong/malformed App or installation labels cause cleanup to leave containers untouched
and reserve their slots. Removing a registration cannot redirect cleanup to another App.
No host cleanup, prune, credential rotation, spending change or consumer migration is
part of this implementation. External artifact storage remains a separate task with
private access and byte/count/retention limits; mandatory uploads are not weakened.

## Reference host activation (2026-11-16)

The maintainer approved stage 2 after the legacy regression smoke passed. The host now
runs the accepted immutable `3a1e352` image, with this explicit registry/account mapping:

| Owner | App ID | Installation ID | Container key path |
|---|---|---|---|
| thiagoluga | 5770327 | 182223102 | /run/secrets/github-app.pem |
| maozinha | 5783289 | 182542055 | /run/secrets/github-app-maozinha.pem |

This deployment preserves the existing single-file thiagoluga secret mount and adds a
second read-only Compose secret instead of relocating its key into the example directory.
The maozinha host file is `/etc/gh-runners-orchestrator/github-app-maozinha.pem`, `0400`,
UID/GID `1654:1654`. Base Compose directly pins the same accepted image digest; ordinary
Compose operations no longer depend on the temporary stage-1 override. The original
`.env` is unchanged and its previous mutable image tag no longer selects the deployed image.

The authenticated maozinha metadata check confirmed private visibility, the expected
permissions, no events and access only to private insurapp. Both installations renewed
separate startup tokens and appeared in stable cycles. The thiagoluga regression smoke
passed with the existing limits and its runner was automatically removed. Insurapp still
uses `CI_RUNNER=gh`; no real maozinha job has run on the pool yet. Full measured evidence,
backup paths and pending live scenarios are in [pool-verification.md](pool-verification.md#separate-private-apps-activated).
