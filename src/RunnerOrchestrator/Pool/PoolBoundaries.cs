using RunnerOrchestrator.Docker;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>The outside world the pool talks to: Docker, GitHub and the clock. Faked in unit tests.</summary>
internal sealed record PoolBoundaries(IContainerRuntime Runtime, IGitHubRunnerApi Api, TimeProvider Time);
