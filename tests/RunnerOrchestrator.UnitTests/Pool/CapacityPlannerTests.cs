using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.UnitTests.Pool;

public sealed class CapacityPlannerTests
{
    private static readonly RepositoryName Alpha = new("thiagoluga", "alpha");
    private static readonly RepositoryName Beta = new("thiagoluga", "beta");
    private static readonly string[] TwoSlots = ["slot-1", "slot-2"];

    [Fact]
    public void Plan_NoDemand_LaunchesNothing()
    {
        var decisions = CapacityPlanner.Plan(TwoSlots, [], [new RepositoryDemand(Alpha, 0, 0, 0)]);

        decisions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_MoreJobsThanSlots_FillsEveryFreeSlotOnce()
    {
        var decisions = CapacityPlanner.Plan(TwoSlots, [], [new RepositoryDemand(Alpha, 3, 0, 0)]);

        decisions.Should().Equal(new LaunchDecision(Alpha, "slot-1"), new LaunchDecision(Alpha, "slot-2"));
    }

    [Fact]
    public void Plan_TakenSlot_IsNotReused()
    {
        var decisions = CapacityPlanner.Plan(TwoSlots, [new PoolContainer(Beta, "slot-1")], [new RepositoryDemand(Alpha, 2, 0, 0)]);

        decisions.Should().Equal(new LaunchDecision(Alpha, "slot-2"));
    }

    [Fact]
    public void Plan_WaitingRunnersCoverQueuedJobs_LaunchesOnlyTheDifference()
    {
        var decisions = CapacityPlanner.Plan(
            ["slot-1", "slot-2", "slot-3"],
            [new PoolContainer(Alpha, "slot-1")],
            [new RepositoryDemand(Alpha, 2, 1, 0)]);

        decisions.Should().Equal(new LaunchDecision(Alpha, "slot-2"));
    }

    [Fact]
    public void Plan_TwoRepositoriesOneSlot_FavorsTheOneWithFewerContainers()
    {
        var decisions = CapacityPlanner.Plan(
            ["slot-1", "slot-2", "slot-3"],
            [new PoolContainer(Alpha, "slot-1"), new PoolContainer(Alpha, "slot-2")],
            [new RepositoryDemand(Alpha, 1, 0, 0), new RepositoryDemand(Beta, 1, 0, 0)]);

        decisions.Should().Equal(new LaunchDecision(Beta, "slot-3"));
    }

    [Fact]
    public void Plan_EqualContainers_AlternatesInConfigurationOrder()
    {
        var decisions = CapacityPlanner.Plan(
            ["slot-1", "slot-2", "slot-3"],
            [],
            [new RepositoryDemand(Alpha, 5, 0, 0), new RepositoryDemand(Beta, 5, 0, 0)]);

        decisions.Select(d => d.Repository).Should().Equal(Alpha, Beta, Alpha);
    }

    [Fact]
    public void Plan_MaxConcurrentReached_LeavesSlotsFree()
    {
        var decisions = CapacityPlanner.Plan(TwoSlots, [new PoolContainer(Alpha, "slot-1")], [new RepositoryDemand(Alpha, 4, 0, 1)]);

        decisions.Should().BeEmpty();
    }
}
