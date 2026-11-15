# The orchestrator (docs/adr/0008-portable-deployment.md). Runs as the non-root `app` user
# (uid 1654) of the chiseled .NET image; the deployment adds the Docker socket's group.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/RunnerOrchestrator/RunnerOrchestrator.csproj src/RunnerOrchestrator/packages.lock.json src/RunnerOrchestrator/
RUN dotnet restore src/RunnerOrchestrator/RunnerOrchestrator.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/RunnerOrchestrator/RunnerOrchestrator.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled@sha256:1b234cef150624fe8cc7d027e8d742c0f03a16aef2484dc469eb8c872167255e
LABEL org.opencontainers.image.source="https://github.com/thiagoluga/gh-runners-orchestrator" \
      org.opencontainers.image.description="Orchestrator of ephemeral GitHub Actions runners" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /app ./
USER app
ENTRYPOINT ["dotnet", "RunnerOrchestrator.dll"]
