using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.GitHub;

/// <summary>The GitHub endpoints the pool uses (ADR 0001, 0004). Faked in unit tests.</summary>
internal interface IGitHubRunnerApi
{
    /// <summary>Budget left according to the last response; null before the first call.</summary>
    RateLimitStatus? RateLimit { get; }

    Task<IReadOnlyList<long>> ListRunIdsAsync(RepositoryName repository, string status, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowJob>> ListJobsAsync(RepositoryName repository, long runId, CancellationToken cancellationToken);

    Task<JitRunner> GenerateJitConfigAsync(RepositoryName repository, string runnerName, IReadOnlyList<string> labels, CancellationToken cancellationToken);

    Task<IReadOnlyList<SelfHostedRunner>> ListRunnersAsync(RepositoryName repository, CancellationToken cancellationToken);

    Task DeleteRunnerAsync(RepositoryName repository, long runnerId, CancellationToken cancellationToken);

    Task<RepositoryInfo> GetRepositoryAsync(RepositoryName repository, CancellationToken cancellationToken);
}

internal sealed record WorkflowJob(long Id, long RunId, string Status, IReadOnlyList<string> Labels, DateTimeOffset? CreatedAt);

internal sealed record SelfHostedRunner(long Id, string Name, string Status, bool Busy);

internal sealed record RepositoryInfo(RepositoryName Name, bool IsPrivate, string OwnerLogin);

internal sealed record RateLimitStatus(int Limit, int Remaining, DateTimeOffset ResetsAt)
{
    /// <summary>ADR 0001: back off below 10% of the budget.</summary>
    public bool IsLow => Limit > 0 && Remaining < Limit / 10;
}

/// <summary>A just-in-time runner. <see cref="ToString"/> hides the configuration.</summary>
internal sealed class JitRunner(long runnerId, string runnerName, string encodedJitConfig)
{
    public long RunnerId { get; } = runnerId;

    public string RunnerName { get; } = runnerName;

    /// <summary>Single-use credential of the runner; goes only into the container environment.</summary>
    public string EncodedJitConfig { get; } = encodedJitConfig;

    public override string ToString() => $"JitRunner {RunnerName} ({RunnerId})";
}
