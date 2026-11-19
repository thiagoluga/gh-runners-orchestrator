using RunnerOrchestrator.Docker;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// The outside world the pool talks to: Docker, GitHub, the clock, and the status it reports
/// (#22). Faked in unit tests.
/// </summary>
internal sealed record PoolBoundaries(IContainerRuntime Runtime, IGitHubRunnerApi Api, TimeProvider Time, PoolStatus Status);
