using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.Docker;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// One pass of the loop: rebuild the pool from Docker labels, remove finished runners, read
/// demand, plan and launch. Holds no state of its own between passes (ADR 0004).
/// </summary>
internal sealed partial class PoolCycle(
    PoolBoundaries boundaries,
    DemandPoller poller,
    RepositoryGuard guard,
    RunnerLauncher launcher,
    IOptions<OrchestratorOptions> options,
    IOptions<RepositoryListOptions> repositories,
    ILogger<PoolCycle> logger)
{
    /// <summary>A launch creates and starts within seconds; a container still only created after this never started.</summary>
    public static readonly TimeSpan StuckCreatedAfter = TimeSpan.FromMinutes(2);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var containers = await boundaries.Runtime.ListByLabelAsync(ManagedLabels.ManagedFilter, cancellationToken).ConfigureAwait(false);
        var live = new List<ContainerSummary>();
        foreach (var container in containers)
        {
            if (container.State is "exited" or "dead" || IsStuckCreated(container))
            {
                await RemoveFinishedAsync(container, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                live.Add(container);
            }
        }

        var rateLimit = boundaries.Api.RateLimit;
        if (rateLimit is { IsLow: true })
        {
            LogBackingOff(logger, rateLimit.Remaining, rateLimit.Limit, rateLimit.ResetsAt);
            return;
        }

        var enabled = await guard.FilterAsync(repositories.Value.Repositories, cancellationToken).ConfigureAwait(false);
        var pool = live.Select(ToPoolContainer).OfType<PoolContainer>().ToList();
        var demands = new List<RepositoryDemand>();
        foreach (var repository in enabled)
        {
            demands.Add(await ReadDemandAsync(repository, live, cancellationToken).ConfigureAwait(false));
        }

        var slots = options.Value.Slots;
        var decisions = CapacityPlanner.Plan([.. slots.Select(s => s.Name)], pool, demands);
        foreach (var decision in decisions)
        {
            var slot = slots.First(s => string.Equals(s.Name, decision.Slot, StringComparison.OrdinalIgnoreCase));
            try
            {
                await launcher.LaunchAsync(decision.Repository, slot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                // Already logged by the launcher; the next cycle tries again.
            }
        }

        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var demand = string.Join(", ", demands.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Repository}={d.QueuedJobs}q/{d.WaitingRunners}w")));
        LogSummary(logger, slots.Count, pool.Count + decisions.Count, demand, boundaries.Api.RateLimit?.Remaining);
    }

    private async Task<RepositoryDemand> ReadDemandAsync(EnabledRepository repository, List<ContainerSummary> live, CancellationToken cancellationToken)
    {
        var queued = await poller.CountQueuedJobsAsync(repository.Name, cancellationToken).ConfigureAwait(false);
        var mine = live.Where(c => ToPoolContainer(c)?.Repository == repository.Name).ToList();
        var waiting = 0;
        if (queued > 0 && mine.Count > 0)
        {
            var runners = await boundaries.Api.ListRunnersAsync(repository.Name, cancellationToken).ConfigureAwait(false);
            var busy = runners.Where(r => r.Busy).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            waiting = mine.Count(c => !c.Labels.TryGetValue(ManagedLabels.RunnerName, out var name) || !busy.Contains(name));
        }

        return new RepositoryDemand(repository.Name, queued, waiting, repository.MaxConcurrent);
    }

    /// <summary>
    /// Records the exit, removes the container with its volumes and deletes the runner
    /// registration. A runner that ran its job already unregistered itself (the delete answers
    /// 404); one that died before its job would otherwise stay registered and offline forever.
    /// Failures are logged and left to the next cycle, so one bad container never stops the pool.
    /// </summary>
    private async Task RemoveFinishedAsync(ContainerSummary container, CancellationToken cancellationToken)
    {
        container.Labels.TryGetValue(ManagedLabels.Repository, out var repositoryLabel);
        try
        {
            var state = await boundaries.Runtime.InspectAsync(container.Id, cancellationToken).ConfigureAwait(false);
            var duration = state.StartedAt is { } started && state.FinishedAt is { } finished ? finished - started : (TimeSpan?)null;
            await boundaries.Runtime.RemoveAsync(container.Id, cancellationToken).ConfigureAwait(false);
            LogFinished(logger, container.Name, repositoryLabel ?? "?", state.ExitCode, duration);
            if (ToPoolContainer(container) is { } pooled
                && container.Labels.TryGetValue(ManagedLabels.RunnerId, out var idLabel)
                && long.TryParse(idLabel, NumberStyles.None, CultureInfo.InvariantCulture, out var runnerId))
            {
                await boundaries.Api.DeleteRunnerAsync(pooled.Repository, runnerId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsTransient(exception, cancellationToken))
        {
            LogRemovalFailed(logger, container.Name, exception.Message);
        }
    }

    /// <summary>Created but never started (start and its cleanup both failed): it must not hold a slot.</summary>
    private bool IsStuckCreated(ContainerSummary container) =>
        container.State == "created"
        && container.Labels.TryGetValue(ManagedLabels.CreatedAt, out var createdLabel)
        && DateTimeOffset.TryParse(createdLabel, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt)
        && boundaries.Time.GetUtcNow() - createdAt > StuckCreatedAfter;

    /// <summary>Failures of one runner that must not stop the cycle; a requested shutdown still does.</summary>
    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception is GitHubApiException or DockerEngineException or HttpRequestException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static PoolContainer? ToPoolContainer(ContainerSummary container)
    {
        if (!container.Labels.TryGetValue(ManagedLabels.Repository, out var repository)
            || !container.Labels.TryGetValue(ManagedLabels.Slot, out var slot))
        {
            return null;
        }

        try
        {
            return new PoolContainer(RepositoryName.Parse(repository), slot);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner {RunnerName} of {Repository} finished with exit code {ExitCode} after {Duration}; container removed")]
    private static partial void LogFinished(ILogger logger, string runnerName, string repository, int exitCode, TimeSpan? duration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Finished runner {RunnerName} not fully removed, retried next cycle: {Reason}")]
    private static partial void LogRemovalFailed(ILogger logger, string runnerName, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit low ({Remaining}/{Limit}); skipping demand until {ResetsAt:O}")]
    private static partial void LogBackingOff(ILogger logger, int remaining, int limit, DateTimeOffset resetsAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cycle: {Busy}/{Slots} slots taken; demand {Demand}; rate limit remaining {Remaining}")]
    private static partial void LogSummary(ILogger logger, int slots, int busy, string demand, int? remaining);
}
