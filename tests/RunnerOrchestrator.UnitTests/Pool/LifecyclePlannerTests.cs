using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.UnitTests.Pool;

public sealed class LifecyclePlannerTests
{
    private static readonly RepositoryName Sandbox = new("thiagoluga", "runner-pool-sandbox");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    [Fact]
    public void Plan_BusyRunnerPastTimeout_IsNeverIdle()
    {
        var plan = Plan([Live("c1", "orch-a", minutesOld: 60)], [new SelfHostedRunner(1, "orch-a", "online", true)]);

        plan.IdleRunners.Should().BeEmpty();
        plan.OrphanContainers.Should().BeEmpty();
    }

    [Fact]
    public void Plan_IdleRunnerBeforeTimeout_IsKept()
    {
        var plan = Plan([Live("c1", "orch-a", minutesOld: 9)], [new SelfHostedRunner(1, "orch-a", "online", false)]);

        plan.IdleRunners.Should().BeEmpty();
    }

    [Fact]
    public void Plan_MissingRunnerWithinGrace_IsNotEvenSuspected()
    {
        var plan = Plan([Live("c1", "orch-a", minutesOld: 1)], []);

        plan.Suspects.Should().BeEmpty();
        plan.OrphanContainers.Should().BeEmpty();
    }

    [Fact]
    public void Plan_MissingRunnerFirstTime_BecomesSuspectOnly()
    {
        var plan = Plan([Live("c1", "orch-a", minutesOld: 5)], []);

        plan.Suspects.Should().BeEquivalentTo(["c1"]);
        plan.OrphanContainers.Should().BeEmpty();
    }

    [Fact]
    public void Plan_OnlineOrBusyOrchRunnerWithoutContainer_IsNotDeleted()
    {
        var plan = Plan([], [
            new SelfHostedRunner(1, "orch-online", "online", false),
            new SelfHostedRunner(2, "orch-busy", "offline", true),
        ]);

        plan.OrphanRunners.Should().BeEmpty();
    }

    [Fact]
    public void Plan_RepositoryWithoutRunnerList_IsLeftAlone()
    {
        var plan = LifecyclePlanner.Plan(Now, [Live("c1", "orch-a", minutesOld: 60)], new Dictionary<RepositoryName, IReadOnlyList<SelfHostedRunner>>(), new HashSet<string> { "c1" }, Idle, Grace);

        plan.IdleRunners.Should().BeEmpty();
        plan.OrphanContainers.Should().BeEmpty();
    }

    private static LifecyclePlan Plan(IReadOnlyList<ManagedRunner> live, IReadOnlyList<SelfHostedRunner> runners) =>
        LifecyclePlanner.Plan(Now, live, new Dictionary<RepositoryName, IReadOnlyList<SelfHostedRunner>> { [Sandbox] = runners }, new HashSet<string>(), Idle, Grace);

    private static ManagedRunner Live(string id, string runnerName, int minutesOld) =>
        new(id, Sandbox, "slot-1", runnerName, 1, Now.AddMinutes(-minutesOld));

    [Fact]
    public void Plan_HomonymousRunnersInDifferentAccounts_OnlyTheMatchingRepositoryHasAContainer()
    {
        var thiago = new RepositoryName("thiagoluga", "shared");
        var mao = new RepositoryName("maozinha", "shared");
        var now = Now;
        var plan = LifecyclePlanner.Plan(now,
            [new ManagedRunner("c", thiago, "slot-1", "orch-same", 7, now)],
            new Dictionary<RepositoryName, IReadOnlyList<SelfHostedRunner>>
            {
                [thiago] = [new SelfHostedRunner(7, "orch-same", "online", true)],
                [mao] = [new SelfHostedRunner(7, "orch-same", "offline", false)],
            }, new HashSet<string>(), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));
        plan.OrphanRunners.Should().ContainSingle().Which.Repository.Should().Be(mao);
        plan.IdleRunners.Should().BeEmpty();
        plan.OrphanContainers.Should().BeEmpty();
    }

    [Fact]
    public void Plan_RegistrationIdDisagreesWithContainerLabel_NeverRemovesTheContainer()
    {
        var repository = new RepositoryName("maozinha", "shared");
        var now = Now;
        var plan = LifecyclePlanner.Plan(now,
            [new ManagedRunner("c", repository, "slot-1", "orch-same", 8, now.AddHours(-1))],
            new Dictionary<RepositoryName, IReadOnlyList<SelfHostedRunner>>
            {
                [repository] = [new SelfHostedRunner(7, "orch-same", "online", false)],
            }, new HashSet<string> { "c" }, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));
        plan.IdleRunners.Should().BeEmpty();
        plan.OrphanContainers.Should().BeEmpty();
        plan.OrphanRunners.Should().BeEmpty();
    }
}
