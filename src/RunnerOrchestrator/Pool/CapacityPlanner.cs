namespace RunnerOrchestrator.Pool;

/// <summary>
/// Pure decision of which repository gets which free slot (ADR 0005). A repository needs a
/// runner for every queued job not already covered by one of its runners that has not picked a
/// job yet. Free slots go one at a time to the repository with the fewest containers; ties go to
/// the repository whose oldest queued job has waited longest, then to the configuration order.
/// </summary>
internal static class CapacityPlanner
{
    public static IReadOnlyList<LaunchDecision> Plan(
        IReadOnlyList<string> slots,
        IReadOnlyList<PoolContainer> live,
        IReadOnlyList<RepositoryDemand> demands)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(demands);

        var taken = live.Select(c => c.Slot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free = new Queue<string>(slots.Where(s => !taken.Contains(s)));
        var state = demands
            .Select((d, order) => new Entry(d, order, live.Count(c => c.Repository == d.Repository), Math.Max(0, d.QueuedJobs - d.WaitingRunners)))
            .ToList();

        var decisions = new List<LaunchDecision>();
        while (free.Count > 0)
        {
            var next = state
                .Where(e => e.Need > 0 && (e.Demand.MaxConcurrent <= 0 || e.Running < e.Demand.MaxConcurrent))
                .OrderBy(e => e.Running)
                .ThenBy(e => e.Demand.OldestQueuedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(e => e.Order)
                .FirstOrDefault();
            if (next is null)
            {
                break;
            }

            decisions.Add(new LaunchDecision(next.Demand.Repository, free.Dequeue()));
            next.Need--;
            next.Running++;
        }

        return decisions;
    }

    private sealed class Entry(RepositoryDemand demand, int order, int running, int need)
    {
        public RepositoryDemand Demand { get; } = demand;

        public int Order { get; } = order;

        public int Running { get; set; } = running;

        public int Need { get; set; } = need;
    }
}

/// <summary>A managed container that still holds its slot (any state but exited).</summary>
internal sealed record PoolContainer(RepositoryName Repository, string Slot);

/// <summary>Queued jobs the pool serves, this repository's runners that have not picked a job yet, and the oldest wait.</summary>
internal sealed record RepositoryDemand(RepositoryName Repository, int QueuedJobs, int WaitingRunners, int MaxConcurrent, DateTimeOffset? OldestQueuedAt = null);

internal sealed record LaunchDecision(RepositoryName Repository, string Slot);
