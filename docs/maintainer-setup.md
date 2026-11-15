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
   repositories** → the toy repository of step 3 only → **Install**.

### Copy the key to the host

From PowerShell on your PC (adjust the downloaded file name):

```powershell
scp -i ~/.ssh/ci_vm_ed25519 "$HOME\Downloads\thiagoluga-runner-pool.2026-11-15.private-key.pem" ci@192.168.68.50:/tmp/github-app.pem
```

On the host (`ssh -i ~/.ssh/ci_vm_ed25519 ci@192.168.68.50`):

```bash
sudo install -d -m 0700 -o 1654 -g 1654 /etc/gh-runners-orchestrator
sudo install -m 0400 -o 1654 -g 1654 /tmp/github-app.pem /etc/gh-runners-orchestrator/github-app.pem
shred -u /tmp/github-app.pem
sudo ls -l /etc/gh-runners-orchestrator/
```

Expect one file, `-r-------- 1654 1654`. UID 1654 is the non-root `app` user of the
official .NET container images, which the orchestrator image runs as.

Then delete the `.pem` from `Downloads` on the PC. A lost key is not a problem: generate a
new one on the App page and delete the old one there.

## 2. SonarQube Cloud

[sonarcloud.io](https://sonarcloud.io) → **+** → **Analyze new project** → your
organization → select `gh-runners-orchestrator` → **Set up**.

| Setting | Value |
|---|---|
| New code definition | **Previous version** |
| Analysis method (Administration → Analysis Method) | **GitHub Actions**; **Automatic Analysis off** (it does not build C#) |
| Main branch (Administration → Branches and Pull Requests) | `main` (GitHub's default branch; check the name before the first analysis) |

Token: your avatar → **My Account** → **Security** → **Generate Tokens**, name
`gh-runners-orchestrator-ci`, then copy it **once**.

GitHub → `gh-runners-orchestrator` → **Settings** → **Secrets and variables** →
**Actions** → **New repository secret**: name `SONAR_TOKEN`, value the token. Do not save
it anywhere else.

Tell the agent the **organization key** and the **project key** shown in the project's
Information page (neither is secret).

## 3. Toy repository

A **private** repository (for example `thiagoluga/runner-pool-sandbox`) with one workflow,
`.github/workflows/pool-smoke.yml`:

```yaml
name: pool-smoke
on: workflow_dispatch
permissions:
  contents: read
jobs:
  smoke:
    runs-on: vm-pool
    timeout-minutes: 10
    steps:
      - run: docker run --rm hello-world
      - run: |
          echo "cores: $(nproc)"
          echo "nofile: $(ulimit -n)"
          free -g
```

Do not dispatch it yet: with no runner it waits in the queue (up to 24 hours). The proof of
concept dispatches it.

## What the agent checks afterwards

```bash
gh api /repos/thiagoluga/runner-pool-sandbox --jq '{private, default_branch}'
gh api /repos/thiagoluga/gh-runners-orchestrator/actions/secrets --jq '.secrets[].name'
```

The App's permissions and installation list are read with a JWT on the host, in the proof
of concept.
