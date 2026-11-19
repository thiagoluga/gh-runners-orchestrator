# Ephemeral GitHub Actions runner started by the orchestrator: one just-in-time runner, one
# job, then the container is removed (docs/adr/0004-ephemeral-runner-lifecycle.md).
# Moved from saas-foundry eng/runners/ with the same pinned base image and checksums.
# Everything the GitHub-hosted ubuntu-24.04 image gives the jobs for free and the jobs use:
# git, curl, jq, unzip, gh, pwsh, the Docker CLI with the Compose and Buildx plugins, and a
# Java runtime for the Sonar scanner.
# The .NET SDK is not here: setup-dotnet installs the pinned one per job.
FROM ubuntu:24.04@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5

ARG RUNNER_VERSION=2.338.2
ARG RUNNER_SHA256=baebf204307bac6193480852732f0aba2ae97b6c5597c244f4d5558390147c40
ARG GH_VERSION=2.104.0
ARG GH_SHA256=98cb101ad87dbbd3f6b2f2bfcbd43197ccd07643140121d8a9cfaa91eadbc24e
ARG PWSH_VERSION=7.7.0
ARG PWSH_SHA256=c89d9c682d2c403dfe726f3df948e359675235a5bd6fab535776de69729ca0cb

LABEL org.opencontainers.image.source="https://github.com/thiagoluga/gh-runners-orchestrator" \
      org.opencontainers.image.description="Ephemeral just-in-time GitHub Actions runner for gh-runners-orchestrator" \
      org.opencontainers.image.licenses="MIT"

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates curl git jq unzip zip tar gzip xz-utils gnupg sudo \
      libicu74 libssl3t64 libkrb5-3 zlib1g openjdk-21-jre-headless \
 && install -m 0755 -d /etc/apt/keyrings \
 && curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc \
 && echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu noble stable" > /etc/apt/sources.list.d/docker.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends docker-ce-cli docker-compose-plugin docker-buildx-plugin \
 && docker compose version && docker buildx version \
 && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL -o /tmp/gh.tgz "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_amd64.tar.gz" \
 && echo "${GH_SHA256}  /tmp/gh.tgz" | sha256sum --check --status \
 && tar -xzf /tmp/gh.tgz -C /tmp && install -m 0755 /tmp/gh_${GH_VERSION}_linux_amd64/bin/gh /usr/local/bin/gh \
 && rm -rf /tmp/gh*

RUN curl -fsSL -o /tmp/pwsh.tgz "https://github.com/PowerShell/PowerShell/releases/download/v${PWSH_VERSION}/powershell-${PWSH_VERSION}-linux-x64.tar.gz" \
 && echo "${PWSH_SHA256}  /tmp/pwsh.tgz" | sha256sum --check --status \
 && mkdir -p /opt/microsoft/powershell/7 && tar -xzf /tmp/pwsh.tgz -C /opt/microsoft/powershell/7 \
 && chmod +x /opt/microsoft/powershell/7/pwsh && ln -s /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
 && rm /tmp/pwsh.tgz

# The runner refuses to run as root, and the jobs have no reason to.
RUN useradd --create-home --uid 1001 --shell /bin/bash runner \
 && mkdir -p /actions-runner && chown runner:runner /actions-runner

USER runner
WORKDIR /actions-runner
RUN curl -fsSL -o runner.tgz "https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/actions-runner-linux-x64-${RUNNER_VERSION}.tar.gz" \
 && echo "${RUNNER_SHA256}  runner.tgz" | sha256sum --check --status \
 && tar -xzf runner.tgz && rm runner.tgz

COPY --chown=runner:runner --chmod=0755 entrypoint.sh /entrypoint.sh

# Testcontainers reaches ports the host Docker publishes on localhost (host network).
ENV RUNNER_MANUALLY_TRAP_SIG=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    TESTCONTAINERS_HOST_OVERRIDE=localhost
ENTRYPOINT ["/entrypoint.sh"]
