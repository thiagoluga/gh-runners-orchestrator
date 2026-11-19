# Maintainer setup

The steps only the maintainer can do: creating credentials and accounts. The agent checks
**metadata** afterwards (permissions, installed repositories, project settings) and never
sees a secret value. Tracked in [#8](https://github.com/thiagoluga/gh-runners-orchestrator/issues/8).

Before pasting a secret in the browser, turn off any writing assistant extension
(Grammarly) for that site.

## 1. GitHub App

GitHub → your avatar → **Settings** → **Developer settings** → **GitHub Apps** →
**New GitHub App**.

| Field | Value |
|---|---|
| GitHub App name | `thiagoluga-runner-pool` (must be unique on GitHub; any free name works) |
| Description | `Creates just-in-time self-hosted runners for gh-runners-orchestrator.` |
| Homepage URL | `https://github.com/thiagoluga/gh-runners-orchestrator` |
| Callback URL | empty |
| Expire user authorization tokens | leave as is (no user authorization is used) |
| Request user authorization (OAuth) during installation | **unchecked** |
| Enable Device Flow | **unchecked** |
| Setup URL | empty |
| Webhook → Active | **unchecked** (polling, [ADR 0001](adr/0001-detect-queued-jobs-by-polling.md)) |

**Permissions → Repository permissions** (everything else stays "No access"):

| Permission | Access |
|---|---|
| Actions | **Read-only** |
| Administration | **Read and write** |
| Metadata | Read-only (mandatory, preselected) |

No organization or account permissions. **Subscribe to events**: none.
**Where can this GitHub App be installed?** → **Only on this account**. → **Create GitHub App**.

On the App's page:

1. Note the **App ID** (a number; not secret).
2. **Private keys** → **Generate a private key**. The browser downloads a `.pem` file.
   That file is the credential. Do not open it in an editor that syncs, and do not paste it
   anywhere.
3. Left menu **Install App** → **Install** next to `thiagoluga` → **Only select
   repositories** → only the private repositories the pool will serve; start with
   `runner-pool-sandbox` (section 3) → **Install**. Never **All repositories**: the App could
   then act on every repository of the account, public ones included.

Adding a repository later: the App → **Install App** → the gear next to the account →
**Repository access** → add it, then list it in `config/repositories.json` on the host
([operations.md](operations.md#enable-or-disable-a-repository)).

### Copy the key to the host

From PowerShell on your PC (adjust the downloaded file name):

```powershell
scp -i ~/.ssh/ci_vm_ed25519 "$HOME\Downloads\thiagoluga-runner-pool.2026-11-15.private-key.pem" ci@192.168.68.66:/tmp/github-app.pem
```

On the pool host `gh-runner-2` (`ssh -i ~/.ssh/ci_vm_ed25519 ci@192.168.68.66`), where
`/etc/gh-runners-orchestrator` already exists with mode 0700 and owner 1654:

```bash
sudo install -m 0400 -o 1654 -g 1654 /tmp/github-app.pem /etc/gh-runners-orchestrator/github-app.pem
shred -u /tmp/github-app.pem
sudo ls -l /etc/gh-runners-orchestrator/
```

Expect one file, `-r-------- 1654 1654`. UID 1654 is the non-root `app` user of the
official .NET container images, which the orchestrator image runs as.

Then delete the `.pem` from `Downloads` on the PC. A lost key is not a problem: generate a
new one on the App page and delete the old one there.

## Additional personal accounts

The initial App setup above is single-account. Before enabling another account, follow
[the metadata and approval gates](multi-account.md#app-installation-gate); do not assume
this App can already be installed there. App ID and installation ID are different values.

## 2. SonarQube Cloud

The project already exists in the organization **`lugarini`**, created before the
repository was renamed (checked 2026-11-15: name `gh-runners-orchestrator`, main branch
`main`, no analysis yet). Its key still carries the old name,
`thiagoluga_gh-runners-orquestrador`.

Rename the key **before the first analysis**, while nothing depends on it: project →
**Administration** → **Update Key** → `thiagoluga_gh-runners-orchestrator`.

| Setting | Value |
|---|---|
| Organization | `lugarini` |
| Project key | `thiagoluga_gh-runners-orchestrator` (after the rename) |
| New code definition | **Previous version** |
| Analysis method (Administration → Analysis Method) | **GitHub Actions**; **Automatic Analysis off** (it does not build C#) |
| Main branch (Administration → Branches and Pull Requests) | `main` (GitHub's default branch; check the name before the first analysis) |

Token: your avatar → **My Account** → **Security** → **Generate Tokens**, name
`gh-runners-orchestrator-ci`, then copy it **once**.

GitHub → `gh-runners-orchestrator` → **Settings** → **Secrets and variables** →
**Actions** → **New repository secret**: name `SONAR_TOKEN`, value the token. Do not save
it anywhere else.

## 3. Integration repository

[`thiagoluga/runner-pool-sandbox`](https://github.com/thiagoluga/runner-pool-sandbox)
(private, created 2026-11-15) holds the pool's end-to-end scenarios: smoke, capacity,
sibling container on `localhost`, cancel and memory limit. All are `workflow_dispatch` only
and aim at `vm-pool`; its README says what each proves.

This repository cannot be the test target itself: it is public, and a fork's pull request
could change its own workflow to `runs-on: vm-pool` and get a runner, because a JIT runner
takes any matching queued job of its repository ([ADR 0006](adr/0006-security-model.md)).

The only maintainer step here is installing the App on it (section 1). Do not dispatch the
scenarios yet: with no runner they wait in the queue for up to 24 hours.

## What the agent checks afterwards

```bash
gh api /repos/thiagoluga/runner-pool-sandbox --jq '{private, default_branch}'
gh api /repos/thiagoluga/gh-runners-orchestrator/actions/secrets --jq '.secrets[].name'
```

The App's permissions and installation list are read with a JWT on the host, in the proof
of concept.

## Multiple private Apps

See [multiple-apps.md](multiple-apps.md) for the App registry, explicit account bindings,
legacy configuration compatibility, key mounts and deployment/rollback approval gates.
The original App can remain private; do not change its visibility for this option.
