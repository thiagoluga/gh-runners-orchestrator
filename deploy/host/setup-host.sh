#!/usr/bin/env bash
# Prepares a fresh Ubuntu 24.04 Server VM as a runner host (docs/new-runner-host.md).
# Run as the admin user (with sudo) on the VM. Idempotent: running it again changes nothing
# that is already right. It never handles a secret: the App keys are copied separately.
#
#   bash setup-host.sh [--ip 192.168.68.70/22 --gateway 192.168.68.1 --dns 192.168.68.1,192.168.15.1]
#
# Without --ip the network is left as the installer set it.
set -euo pipefail

ip="" gateway="" dns=""
while [ $# -gt 0 ]; do
  case "$1" in
    --ip) ip="$2"; shift 2 ;;
    --gateway) gateway="$2"; shift 2 ;;
    --dns) dns="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

step() { printf '\n== %s\n' "$*"; }

. /etc/os-release
if [ "${ID:-}" != ubuntu ] || [ "${VERSION_CODENAME:-}" != noble ]; then
  echo "This script supports Ubuntu 24.04 (noble) only; found ${PRETTY_NAME:-unknown}." >&2
  exit 1
fi
sudo -n true 2>/dev/null || { echo "The admin user needs passwordless sudo (see the runbook)." >&2; exit 1; }
user=$(id -un)

step "Packages and automatic security updates"
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ca-certificates curl unattended-upgrades >/dev/null
sudo systemctl enable --now unattended-upgrades >/dev/null
sudo timedatectl set-timezone Etc/UTC

step "Docker Engine from Docker's repository"
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
  sudo install -m 0755 -d /etc/apt/keyrings
  sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  sudo chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu noble stable" \
    | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
  sudo apt-get update -qq
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin >/dev/null
fi
daemon='{
  "default-ulimits": { "nofile": { "Name": "nofile", "Hard": 65536, "Soft": 65536 } },
  "log-driver": "json-file",
  "log-opts": { "max-size": "20m", "max-file": "3" }
}'
if [ "$(sudo cat /etc/docker/daemon.json 2>/dev/null)" != "$daemon" ]; then
  # nofile 65536: 1024 kept Aspire hosts from starting (docs/ai/core.md, lesson 1).
  echo "$daemon" | sudo tee /etc/docker/daemon.json >/dev/null
  sudo systemctl restart docker
fi
sudo systemctl enable --now docker >/dev/null
id -nG "$user" | grep -qw docker || sudo usermod -aG docker "$user"
[ "$(stat -fc %T /sys/fs/cgroup)" = cgroup2fs ] || { echo "cgroup v2 is required." >&2; exit 1; }

step "SSH by key only"
if [ -s "$HOME/.ssh/authorized_keys" ]; then
  conf='PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no'
  if [ "$(sudo cat /etc/ssh/sshd_config.d/10-runner-host.conf 2>/dev/null)" != "$conf" ]; then
    echo "$conf" | sudo tee /etc/ssh/sshd_config.d/10-runner-host.conf >/dev/null
    sudo sshd -t && sudo systemctl reload ssh
  fi
else
  echo "No ~/.ssh/authorized_keys: password login left on. Add your key, then run this again." >&2
fi

if [ -n "$ip" ]; then
  step "Static address $ip"
  [ -n "$gateway" ] && [ -n "$dns" ] || { echo "--ip needs --gateway and --dns." >&2; exit 2; }
  nic=$(ip -o route show default | awk '{print $5; exit}')
  plan="network:
  version: 2
  ethernets:
    $nic:
      dhcp4: false
      addresses: [$ip]
      routes:
        - to: default
          via: $gateway
      nameservers:
        addresses: [${dns//,/, }]"
  if [ "$(sudo cat /etc/netplan/50-runner-host.yaml 2>/dev/null)" != "$plan" ]; then
    sudo find /etc/netplan -maxdepth 1 -name '*.yaml' ! -name 50-runner-host.yaml -exec mv {} {}.disabled \;
    echo "$plan" | sudo tee /etc/netplan/50-runner-host.yaml >/dev/null
    sudo chmod 600 /etc/netplan/50-runner-host.yaml
    echo "The address changes to $ip when netplan applies; reconnect there if this session drops."
    sudo netplan apply
  fi
fi

step "Orchestrator folders"
mkdir -p "$HOME/gh-runners-orchestrator/config"
# Keys live here: directory 0700 and files 0400, owned by the orchestrator's uid 1654.
sudo install -d -m 0700 -o 1654 -g 1654 /etc/gh-runners-orchestrator

step "Ready"
printf 'host %s, %s vCPUs, %s GiB, Docker %s, docker group gid %s\n' \
  "$(hostname)" "$(nproc)" "$(free -g | awk '/Mem/{print $2}')" \
  "$(sudo docker version --format '{{.Server.Version}}')" "$(stat -c %g /var/run/docker.sock)"
