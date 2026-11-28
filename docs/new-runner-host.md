# Add a runner host

How to create one more VM for the pool and put it to work next to the existing hosts
([ADR 0013](adr/0013-peer-orchestrators-on-several-hosts.md)). Three steps:

1. **Create the VM** on the Hyper-V machine: one script.
2. **Install Ubuntu** in the VM console: about ten installer screens, then two commands.
3. **Add it to the pool** from the admin PC: one script does the rest.

What the result looks like: Ubuntu 24.04 Server with Docker Engine, SSH by key only, user
`ci` with passwordless sudo, the orchestrator pinned to the same image as the other hosts,
the App keys copied host to host, and every host's `PoolHosts` listing the new one.

## Before you start

- An Ubuntu Server 24.04 ISO (`ubuntu-24.04.x-live-server-amd64.iso`) on the Hyper-V
  machine.
- A free address on the pool's network (today `192.168.68.0/22`, gateway `192.168.68.1`,
  DNS `192.168.68.1` and `192.168.15.1`), and a short host name: lower-case letters,
  digits and single hyphens, at most 16 characters, not an account name (for example
  `runner-3`).
- On the admin PC: PowerShell 7, the OpenSSH client and the admin key
  `~/.ssh/ci_vm_ed25519` that reaches the existing hosts as `ci`.
- Size: give each slot its own cores and memory and leave room for the host and for the
  containers jobs start beside the runner (PostgreSQL, Testcontainers). The hosts today
  use 5–6 cores and 16 GiB per slot, and keep 2 or more cores and 10 GiB or more free.

## 1. Create the VM (Hyper-V machine)

In an elevated PowerShell on the Hyper-V machine, from a copy of this repository:

```powershell
.\deploy\hyperv\New-RunnerVm.ps1 -Name runner-3 -Cpus 12 -MemoryGB 32 -IsoPath D:\iso\ubuntu-24.04.3-live-server-amd64.iso
```

It creates a generation 2 VM with Secure Boot (UEFI CA template), fixed memory, a 200 GB
disk (`-DiskGB` to change), no automatic checkpoints, starting with Windows, on the only
external switch (`-SwitchName` when there are several), and boots the installer. Open the
console with `vmconnect.exe localhost runner-3`.

## 2. Install Ubuntu (VM console)

Take the defaults except where the table says otherwise.

| Screen | Choice |
|---|---|
| Language, keyboard | English; your keyboard layout |
| Type of installation | **Ubuntu Server** (not minimized) |
| Network | DHCP is fine (the script can set the static address later), or **Edit IPv4 → Manual** with the address, `/22` subnet, gateway and DNS above |
| Proxy, mirror | empty; default mirror |
| Storage | **Use an entire disk**, and **uncheck "Set up this disk as an LVM group"** (with LVM the installer uses only part of the disk) |
| Profile | your name; server name = the host name (`runner-3`); user name **`ci`**; a password you keep for the console |
| Ubuntu Pro | skip |
| SSH | **Install OpenSSH server**; no key import |
| Featured snaps | none (Docker comes from Docker's repository, not the snap) |

When it says **Reboot now**, detach the ISO so the VM boots from its disk. On the Hyper-V
machine:

```powershell
Get-VMDvdDrive -VMName runner-3 | Set-VMDvdDrive -Path $null
Set-VMFirmware -VMName runner-3 -FirstBootDevice (Get-VMHardDiskDrive -VMName runner-3)
```

Then, from the admin PC, authorize the admin key and give `ci` passwordless sudo (each asks
the password you chose once; use the VM's current address):

```powershell
Get-Content $HOME\.ssh\ci_vm_ed25519.pub | ssh ci@192.168.68.71 "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
```

```powershell
ssh -t ci@192.168.68.71 "echo 'ci ALL=(ALL) NOPASSWD:ALL' | sudo tee /etc/sudoers.d/ci >/dev/null && sudo chmod 440 /etc/sudoers.d/ci"
```

## 3. Add it to the pool (admin PC)

From a copy of this repository, list every host already in the pool after
`-ExistingHosts`, the oldest first (it is the source of the settings and the image):

```powershell
.\deploy\host\Add-RunnerHost.ps1 -Address 192.168.68.71 -Name runner-3 -Slots '0-4:16g;5-9:16g' -ExistingHosts 192.168.68.66,192.168.68.50 -StaticIp 192.168.68.70/22 -Gateway 192.168.68.1 -Dns 192.168.68.1,192.168.15.1
```

`-Slots` is `cpuset:memory` per slot, separated by `;`. Leave out `-StaticIp`, `-Gateway`
and `-Dns` when the installer already set the final address. What it does, in order:

1. Reads `config/orchestrator.json` of every existing host and works out the new
   `PoolHosts` (existing hosts first, the new one last).
2. Copies [`setup-host.sh`](../deploy/host/setup-host.sh) to the VM and runs it:
   automatic security updates, UTC, Docker Engine and Compose from Docker's repository,
   `nofile` 65536, the `ci` user in the `docker` group, SSH by key only, the static address
   if given, and the orchestrator folders. It refuses anything but Ubuntu 24.04.
3. Writes the new host's settings: the first host's compose file (so the same pinned
   image), repository list, `.env` with the new host's Docker group, and
   `orchestrator.json` with its own `Host`, `PoolHosts` and slots.
4. Copies each App key file named in the compose file from the first host to the new
   one, through a pipe on the PC that never shows or stores it, and checks that both
   hashes match. Mode `0400`, owner `1654`.
5. Adds the new host to `PoolHosts` on the existing hosts (backup
   `config/orchestrator.json.before-<name>`) and restarts them, **before** the new host
   starts.
6. Starts the new host and checks `/healthz` on every host.

Running it again is safe: it repeats only what is not already in place. With `-WhatIf` it
reads the existing hosts, checks the names and prints what it would do, and changes
nothing. Before changing anything it refuses a host name the orchestrator would reject (an
account name, or not 1-16 lower-case letters, digits and single hyphens), so a bad name
never restarts the other hosts into a configuration they refuse.

Then run the sandbox `smoke` workflow a few times. The new host serves jobs whose id
belongs to it (`PoolHosts[job id mod N]`), so within a few runs one of its
`orch-<name>--…` runners shows up in the run's job list.

## Remove a host

1. On that host: `docker compose stop`, and let its running jobs finish.
2. On every other host: remove it from `PoolHosts` in `config/orchestrator.json` and
   `docker compose restart`.
3. On that host: `docker compose down`, and delete the key files in
   `/etc/gh-runners-orchestrator/` when it leaves the pool for good (confirm first: a
   deletion of a key copy cannot be undone, the key itself stays on the other hosts).

## Not verified

- `New-RunnerVm.ps1` has not been run yet: the Hyper-V machine is not the admin PC.
- `Add-RunnerHost.ps1` and `setup-host.sh` were run against `github-runner`, a host that
  was already set up (idempotent pass, the key copy with one key moved aside), not yet on a
  fresh installation or with `-StaticIp`.
