using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// Pure decision of what to clean up among live runners (ADR 0004):
/// <list type="bullet">
/// <item>idle: the runner exists, has not picked a job, and is older than the idle timeout
/// (a job cancelled before pickup, or taken by another runner);</item>
/// <item>orphan container: its runner is gone from GitHub on two cycles in a row, past a grace
/// period (deleted by hand, or a container that outlived its job);</item>
/// <item>orphan runner: an offline, idle <c>orch-</c> runner with no container (a container
/// removed by hand, or a crash between registration and start).</item>
/// </list>
/// Repositories whose runner list could not be read are left alone this cycle.
/// </summary>
internal static class LifecyclePlanner
{
    public const string RunnerPrefix = "orch-";

    public static LifecyclePlan Plan(
        DateTimeOffset now,
        IReadOnlyList<ManagedRunner> live,
        IReadOnlyDictionary<RepositoryName, IReadOnlyList<SelfHostedRunner>> runnersByRepository,
        IReadOnlySet<string> suspects,
        TimeSpan idleTimeout,
        TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(runnersByRepository);
        ArgumentNullException.ThrowIfNull(suspects);

        var idle = new List<ManagedRunner>();
        var orphanContainers = new List<ManagedRunner>();
        var newSuspects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in live)
        {
            if (!runnersByRepository.TryGetValue(container.Repository, out var runners))
            {
                continue;
            }

            var age = container.CreatedAt is { } created ? now - created : TimeSpan.Zero;
            var runner = runners.FirstOrDefault(r => string.Equals(r.Name, container.RunnerName, StringComparison.OrdinalIgnoreCase));
            switch (Classify(runner, age, suspects.Contains(container.ContainerId), idleTimeout, grace))
            {
                case ContainerVerdict.Idle:
                    idle.Add(container);
                    break;
                case ContainerVerdict.Orphan:
                    orphanContainers.Add(container);
                    break;
                case ContainerVerdict.Suspect:
                    newSuspects.Add(container.ContainerId);
                    break;
                default:
                    break;
            }
        }

        var withContainer = live.Select(c => c.RunnerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphanRunners = runnersByRepository
            .SelectMany(pair => pair.Value.Select(runner => new OrphanRunner(pair.Key, runner)))
            .Where(o => o.Runner.Name.StartsWith(RunnerPrefix, StringComparison.OrdinalIgnoreCase)
                && !o.Runner.Busy
                && string.Equals(o.Runner.Status, "offline", StringComparison.OrdinalIgnoreCase)
                && !withContainer.Contains(o.Runner.Name))
            .ToList();

        return new LifecyclePlan(idle, orphanContainers, orphanRunners, newSuspects);
    }

    private static ContainerVerdict Classify(SelfHostedRunner? runner, TimeSpan age, bool wasSuspect, TimeSpan idleTimeout, TimeSpan grace)
    {
        if (runner is not null)
        {
            return !runner.Busy && age > idleTimeout ? ContainerVerdict.Idle : ContainerVerdict.Keep;
        }

        if (age <= grace)
        {
            return ContainerVerdict.Keep;
        }

        return wasSuspect ? ContainerVerdict.Orphan : ContainerVerdict.Suspect;
    }

    private enum ContainerVerdict
    {
        Keep,
        Idle,
        Suspect,
        Orphan,
    }
}

/// <summary>A live managed container, read from its labels.</summary>
internal sealed record ManagedRunner(
    string ContainerId,
    RepositoryName Repository,
    string Slot,
    string RunnerName,
    long? RunnerId,
    DateTimeOffset? CreatedAt);

internal sealed record OrphanRunner(RepositoryName Repository, SelfHostedRunner Runner);

internal sealed record LifecyclePlan(
    IReadOnlyList<ManagedRunner> IdleRunners,
    IReadOnlyList<ManagedRunner> OrphanContainers,
    IReadOnlyList<OrphanRunner> OrphanRunners,
    IReadOnlySet<string> Suspects);
