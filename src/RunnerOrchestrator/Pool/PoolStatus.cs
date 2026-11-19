namespace RunnerOrchestrator.Pool;

/// <summary>
/// What the last cycles saw, for the status endpoint (#22). Written by the cycle and the worker,
/// read by the endpoint; every read gets a consistent snapshot. Holds counters and names only,
/// never a token or a runner configuration.
/// </summary>
internal sealed class PoolStatus
{
    private readonly Lock _gate = new();
    private PoolStatusSnapshot _snapshot = new();

    public PoolStatusSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    /// <summary>The cycle ended without an exception, including one skipped for a low rate budget.</summary>
    public void CycleSucceeded(DateTimeOffset at) => Update(s => s with
    {
        Cycles = s.Cycles + 1,
        LastSuccessfulCycleAt = at,
    });

    public void CycleFailed(DateTimeOffset at) => Update(s => s with
    {
        Cycles = s.Cycles + 1,
        CycleFailures = s.CycleFailures + 1,
        LastFailedCycleAt = at,
    });

    public void RateLimited(bool limited) => Update(s => s with { RateLimited = limited });

    /// <summary>A managed container has no configured slot; launches stop until it is resolved.</summary>
    public void Blocked(bool blocked) => Update(s => s with { Blocked = blocked });

    public void RunnerFinished() => Update(s => s with { RunnersFinished = s.RunnersFinished + 1 });

    public void LaunchFailed() => Update(s => s with { LaunchFailures = s.LaunchFailures + 1 });

    public void PoolObserved(int slots, int busy, int launched, IReadOnlyList<RepositoryDemand> demands, IReadOnlyDictionary<string, int?> rateRemaining) =>
        Update(s => s with
        {
            Slots = slots,
            BusySlots = busy,
            RunnersLaunched = s.RunnersLaunched + launched,
            Demand = [.. demands],
            RateRemaining = new Dictionary<string, int?>(rateRemaining, StringComparer.OrdinalIgnoreCase),
        });

    /// <summary>A cycle that read no demand (rate budget low): slots are current, demand is unknown.</summary>
    public void SlotsObserved(int slots, int busy) => Update(s => s with
    {
        Slots = slots,
        BusySlots = busy,
        Demand = [],
    });

    private void Update(Func<PoolStatusSnapshot, PoolStatusSnapshot> change)
    {
        lock (_gate)
        {
            _snapshot = change(_snapshot);
        }
    }
}

internal sealed record PoolStatusSnapshot
{
    public long Cycles { get; init; }

    public long CycleFailures { get; init; }

    public DateTimeOffset? LastSuccessfulCycleAt { get; init; }

    public DateTimeOffset? LastFailedCycleAt { get; init; }

    public bool RateLimited { get; init; }

    public bool Blocked { get; init; }

    public int Slots { get; init; }

    public int BusySlots { get; init; }

    public long RunnersLaunched { get; init; }

    public long RunnersFinished { get; init; }

    public long LaunchFailures { get; init; }

    public IReadOnlyList<RepositoryDemand> Demand { get; init; } = [];

    /// <summary>Remaining GitHub API budget per account, null while unknown.</summary>
    public IReadOnlyDictionary<string, int?> RateRemaining { get; init; } = new Dictionary<string, int?>();
}
