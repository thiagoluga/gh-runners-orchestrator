using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.Docker;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// One pass of the loop: rebuild the pool from Docker labels, remove finished runners, clean up
/// idle and orphaned ones, read demand, plan and launch. The only state kept between passes is
/// the set of containers whose runner was missing last time (ADR 0004); a restart loses nothing
/// but one cycle of that.
/// </summary>
internal sealed partial class PoolCycle(
    PoolBoundaries boundaries,
    DemandPoller poller,
    RepositoryGuard guard,
    RunnerLauncher launcher,
    IOptions<OrchestratorOptions> options,
    IOptionsMonitor<RepositoryListOptions> repositories,
    ILogger<PoolCycle> logger)
{
    /// <summary>
    /// A launch creates and starts within seconds; a container still only created after this
    /// never started. Also the grace before a container whose runner is missing becomes a suspect.
    /// </summary>
    public static readonly TimeSpan StuckCreatedAfter = TimeSpan.FromMinutes(2);

    private IReadOnlySet<string> _suspects = new HashSet<string>(StringComparer.Ordinal);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var containers = await boundaries.Runtime.ListByLabelAsync(ManagedLabels.ManagedFilter, cancellationToken).ConfigureAwait(false);
        var live = new List<ManagedRunner>();
        foreach (var container in containers)
        {
            if (container.State is "exited" or "dead" || IsStuckCreated(container))
            {
                await RemoveFinishedAsync(container, cancellationToken).ConfigureAwait(false);
            }
            else if (ToManagedRunner(container) is { } managed)
            {
                live.Add(managed);
            }
        }

        var rateLimit = boundaries.Api.RateLimit;
        if (rateLimit is { IsLow: true })
        {
            LogBackingOff(logger, rateLimit.Remaining, rateLimit.Limit, rateLimit.ResetsAt);
            return;
        }

        var settings = options.Value;
        var enabled = await guard.FilterAsync(repositories.CurrentValue.Repositories, cancellationToken).ConfigureAwait(false);
        var watched = enabled.Select(e => e.Name)
            .Concat(live.Select(c => c.Repository).Where(r => string.Equals(r.Owner, settings.Owner, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();
        var runners = await ReadRunnersAsync(watched, cancellationToken).ConfigureAwait(false);

        var lifecycle = LifecyclePlanner.Plan(boundaries.Time.GetUtcNow(), live, runners, _suspects, settings.IdleRunnerTimeout, StuckCreatedAfter);
        _suspects = lifecycle.Suspects;
        var removed = await ApplyLifecycleAsync(lifecycle, runners, cancellationToken).ConfigureAwait(false);
        live.RemoveAll(c => removed.Contains(c.ContainerId));

        var demands = new List<RepositoryDemand>();
        foreach (var repository in enabled)
        {
            demands.Add(await ReadDemandAsync(repository, live, runners, cancellationToken).ConfigureAwait(false));
        }

        var slots = settings.Slots;
        var pool = live.Select(c => new PoolContainer(c.Repository, c.Slot)).ToList();
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

    private async Task<Dictionary<RepositoryName, IReadOnlyList<SelfHostedRunner>>> ReadRunnersAsync(List<RepositoryName> watched, CancellationToken cancellationToken)
    {
        var runners = new Dictionary<RepositoryName, IReadOnlyList<SelfHostedRunner>>();
        foreach (var repository in watched)
        {
            try
            {
                runners[repository] = await boundaries.Api.ListRunnersAsync(repository, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                // Without the runner list nothing of this repository is cleaned up this cycle.
                LogRunnersUnreadable(logger, repository, exception.Message);
            }
        }

        return runners;
    }

    private async Task<HashSet<string>> ApplyLifecycleAsync(
        LifecyclePlan plan,
        Dictionary<RepositoryName, IReadOnlyList<SelfHostedRunner>> runners,
        CancellationToken cancellationToken)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in plan.IdleRunners)
        {
            try
            {
                // Runner first: GitHub refuses to delete a runner that just picked a job, and then
                // the container must stay.
                var runnerId = container.RunnerId ?? runners[container.Repository].First(r => string.Equals(r.Name, container.RunnerName, StringComparison.OrdinalIgnoreCase)).Id;
                await boundaries.Api.DeleteRunnerAsync(container.Repository, runnerId, cancellationToken).ConfigureAwait(false);
                await boundaries.Runtime.RemoveAsync(container.ContainerId, cancellationToken).ConfigureAwait(false);
                removed.Add(container.ContainerId);
                LogIdleRemoved(logger, container.RunnerName, container.Repository);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                LogCleanupFailed(logger, container.RunnerName, exception.Message);
            }
        }

        foreach (var container in plan.OrphanContainers)
        {
            try
            {
                await boundaries.Runtime.RemoveAsync(container.ContainerId, cancellationToken).ConfigureAwait(false);
                removed.Add(container.ContainerId);
                LogOrphanContainerRemoved(logger, container.RunnerName, container.Repository);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                LogCleanupFailed(logger, container.RunnerName, exception.Message);
            }
        }

        foreach (var orphan in plan.OrphanRunners)
        {
            try
            {
                await boundaries.Api.DeleteRunnerAsync(orphan.Repository, orphan.Runner.Id, cancellationToken).ConfigureAwait(false);
                LogOrphanRunnerDeleted(logger, orphan.Runner.Name, orphan.Repository);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                LogCleanupFailed(logger, orphan.Runner.Name, exception.Message);
            }
        }

        return removed;
    }

    private async Task<RepositoryDemand> ReadDemandAsync(
        EnabledRepository repository,
        List<ManagedRunner> live,
        Dictionary<RepositoryName, IReadOnlyList<SelfHostedRunner>> runners,
        CancellationToken cancellationToken)
    {
        var queued = await poller.ReadQueuedJobsAsync(repository.Name, cancellationToken).ConfigureAwait(false);
        var busy = runners.TryGetValue(repository.Name, out var list)
            ? list.Where(r => r.Busy).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var waiting = live.Count(c => c.Repository == repository.Name && !busy.Contains(c.RunnerName));
        return new RepositoryDemand(repository.Name, queued.Count, waiting, repository.MaxConcurrent, queued.OldestQueuedAt);
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
            if (ToManagedRunner(container) is { RunnerId: { } runnerId } managed)
            {
                await DeleteFinishedRegistrationAsync(managed, runnerId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsTransient(exception, cancellationToken))
        {
            LogRemovalFailed(logger, container.Name, exception.Message);
        }
    }

    /// <summary>
    /// A runner killed in the middle of a job stays "busy" on GitHub until GitHub gives the job up,
    /// and GitHub refuses to delete it until then (422). The container is already gone, so the
    /// orphan sweep deletes the registration once GitHub releases it.
    /// </summary>
    private async Task DeleteFinishedRegistrationAsync(ManagedRunner runner, long runnerId, CancellationToken cancellationToken)
    {
        try
        {
            await boundaries.Api.DeleteRunnerAsync(runner.Repository, runnerId, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException exception)
        {
            LogRegistrationLeftToSweep(logger, runner.RunnerName, exception.Message);
        }
    }

    /// <summary>Created but never started (start and its cleanup both failed): it must not hold a slot.</summary>
    private bool IsStuckCreated(ContainerSummary container) =>
        container.State == "created"
        && ToManagedRunner(container) is { CreatedAt: { } createdAt }
        && boundaries.Time.GetUtcNow() - createdAt > StuckCreatedAfter;

    /// <summary>Failures of one runner that must not stop the cycle; a requested shutdown still does.</summary>
    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception is GitHubApiException or DockerEngineException or HttpRequestException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static ManagedRunner? ToManagedRunner(ContainerSummary container)
    {
        var labels = container.Labels;
        if (!labels.TryGetValue(ManagedLabels.Repository, out var repository)
            || !labels.TryGetValue(ManagedLabels.Slot, out var slot))
        {
            return null;
        }

        if (RepositoryName.TryParse(repository) is not { } name)
        {
            return null;
        }

        long? runnerId = labels.TryGetValue(ManagedLabels.RunnerId, out var idLabel)
            && long.TryParse(idLabel, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        DateTimeOffset? createdAt = labels.TryGetValue(ManagedLabels.CreatedAt, out var createdLabel)
            && DateTimeOffset.TryParse(createdLabel, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created) ? created : null;
        var runnerName = labels.TryGetValue(ManagedLabels.RunnerName, out var runnerLabel) ? runnerLabel : container.Name;
        return new ManagedRunner(container.Id, name, slot, runnerName, runnerId, createdAt);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner {RunnerName} of {Repository} finished with exit code {ExitCode} after {Duration}; container removed")]
    private static partial void LogFinished(ILogger logger, string runnerName, string repository, int exitCode, TimeSpan? duration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Finished runner {RunnerName} not fully removed, retried next cycle: {Reason}")]
    private static partial void LogRemovalFailed(ILogger logger, string runnerName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registration of runner {RunnerName} not deleted yet ({Reason}); the orphan sweep deletes it once GitHub releases it")]
    private static partial void LogRegistrationLeftToSweep(ILogger logger, string runnerName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner {RunnerName} of {Repository} picked no job before the idle timeout; removed")]
    private static partial void LogIdleRemoved(ILogger logger, string runnerName, RepositoryName repository);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Container of runner {RunnerName} of {Repository} had no runner on GitHub for two cycles; removed")]
    private static partial void LogOrphanContainerRemoved(ILogger logger, string runnerName, RepositoryName repository);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offline runner {RunnerName} of {Repository} had no container; deleted")]
    private static partial void LogOrphanRunnerDeleted(ILogger logger, string runnerName, RepositoryName repository);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleaning up runner {RunnerName} failed, retried next cycle: {Reason}")]
    private static partial void LogCleanupFailed(ILogger logger, string runnerName, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runners of {Repository} not readable this cycle: {Reason}")]
    private static partial void LogRunnersUnreadable(ILogger logger, RepositoryName repository, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit low ({Remaining}/{Limit}); skipping demand until {ResetsAt:O}")]
    private static partial void LogBackingOff(ILogger logger, int remaining, int limit, DateTimeOffset resetsAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cycle: {Busy}/{Slots} slots taken; demand {Demand}; rate limit remaining {Remaining}")]
    private static partial void LogSummary(ILogger logger, int slots, int busy, string demand, int? remaining);
}
