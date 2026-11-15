using Microsoft.Extensions.Time.Testing;
using RunnerOrchestrator.Docker;
using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.Pool;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests.Pool;

public sealed class PoolCycleTests
{
    private static readonly RepositoryName Sandbox = new("thiagoluga", "runner-pool-sandbox");
    private readonly IGitHubRunnerApi _api = Substitute.For<IGitHubRunnerApi>();
    private readonly IContainerRuntime _runtime = Substitute.For<IContainerRuntime>();
    private readonly ListLogger<PoolCycle> _logger = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

    public PoolCycleTests()
    {
        _api.GetRepositoryAsync(Sandbox, Arg.Any<CancellationToken>()).Returns(new RepositoryInfo(Sandbox, true, "thiagoluga"));
        _api.ListRunIdsAsync(Sandbox, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _api.GenerateJitConfigAsync(Sandbox, Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => new JitRunner(1, call.ArgAt<string>(1), "jit"));
        _runtime.ListByLabelAsync("orchestrator.managed=true", Arg.Any<CancellationToken>()).Returns([]);
        _runtime.CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>()).Returns("new");
    }

    [Fact]
    public async Task RunOnce_ExitedContainer_IsInspectedAndRemovedWithItsExitCode()
    {
        _runtime.ListByLabelAsync("orchestrator.managed=true", Arg.Any<CancellationToken>()).Returns([Container("c1", "exited", "slot-1")]);
        _runtime.InspectAsync("c1", Arg.Any<CancellationToken>()).Returns(new ContainerState(
            "exited", 0, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 15, 12, 3, 0, TimeSpan.Zero)));

        await Create().RunOnceAsync(TestContext.Current.CancellationToken);

        await _runtime.Received(1).RemoveAsync("c1", Arg.Any<CancellationToken>());
        await _api.Received(1).DeleteRunnerAsync(Sandbox, 7, Arg.Any<CancellationToken>());
        _logger.AllText.Should().Contain("finished with exit code 0 after 00:03:00");
    }

    [Fact]
    public async Task RunOnce_FinishedContainerVanishesBeforeInspect_LogsAndStillLaunches()
    {
        QueueJobs(1);
        _runtime.ListByLabelAsync("orchestrator.managed=true", Arg.Any<CancellationToken>()).Returns([Container("c1", "exited", "slot-1")]);
        _runtime.InspectAsync("c1", Arg.Any<CancellationToken>()).Returns<ContainerState>(_ => throw new DockerEngineException("Docker answered 404"));

        await Create().RunOnceAsync(TestContext.Current.CancellationToken);

        _logger.AllText.Should().Contain("not fully removed");
        await _runtime.Received(1).CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnce_LaunchTimesOut_DoesNotStopTheCycle()
    {
        QueueJobs(2);
        _runtime.CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>()).Returns<string>(_ => throw new TaskCanceledException("timeout"));

        await Create().RunOnceAsync(TestContext.Current.CancellationToken);

        await _runtime.Received(2).CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>());
        await _api.Received(2).DeleteRunnerAsync(Sandbox, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnce_TwoQueuedJobs_LaunchesOneRunnerPerFreeSlot()
    {
        QueueJobs(3);

        await Create().RunOnceAsync(TestContext.Current.CancellationToken);

        await _runtime.Received(2).CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>());
        _logger.AllText.Should().Contain("2/2 slots taken").And.Contain("thiagoluga/runner-pool-sandbox=3q/0w");
    }

    [Fact]
    public async Task RunOnce_RunnerWaitingForTheOnlyJob_LaunchesNothing()
    {
        QueueJobs(1);
        _runtime.ListByLabelAsync("orchestrator.managed=true", Arg.Any<CancellationToken>()).Returns([Container("c1", "running", "slot-1")]);
        _api.ListRunnersAsync(Sandbox, Arg.Any<CancellationToken>()).Returns([new SelfHostedRunner(1, "orch-c1", "online", false)]);

        await Create().RunOnceAsync(TestContext.Current.CancellationToken);

        await _runtime.DidNotReceiveWithAnyArgs().CreateAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunOnce_BusyRunnerAndOneMoreJob_LaunchesInTheOtherSlot()
    {
        QueueJobs(1);
        _runtime.ListByLabelAsync("orchestrator.managed=true", Arg.Any<CancellationToken>()).Returns([Container("c1", "running", "slot-1")]);
        _api.ListRunnersAsync(Sandbox, Arg.Any<CancellationToken>()).Returns([new SelfHostedRunner(1, "orch-c1", "online", true)]);

        await Create().RunOnceAsync(TestContext.Current.CancellationToken);

        await _runtime.Received(1).CreateAsync(Arg.Is<ContainerSpec>(s => s.Cpuset == "5-9"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnce_RateLimitLow_SkipsDemandButStillRemovesFinishedContainers()
    {
        _api.RateLimit.Returns(new RateLimitStatus(5000, 100, DateTimeOffset.UnixEpoch));
        _runtime.ListByLabelAsync("orchestrator.managed=true", Arg.Any<CancellationToken>()).Returns([Container("c1", "dead", "slot-1")]);
        _runtime.InspectAsync("c1", Arg.Any<CancellationToken>()).Returns(new ContainerState("dead", 137, null, null));

        await Create().RunOnceAsync(TestContext.Current.CancellationToken);

        await _runtime.Received(1).RemoveAsync("c1", Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().ListRunIdsAsync(default, default!, TestContext.Current.CancellationToken);
        _logger.AllText.Should().Contain("Rate limit low");
    }

    [Fact]
    public async Task RunOnce_LaunchFails_DoesNotStopTheCycle()
    {
        QueueJobs(1);
        _runtime.CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>()).Returns<string>(_ => throw new DockerEngineException("no space"));

        var act = () => Create().RunOnceAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunOnce_ContainerStuckInCreated_IsRemovedAndFreesItsSlot()
    {
        QueueJobs(1);
        _runtime.ListByLabelAsync("orchestrator.managed=true", Arg.Any<CancellationToken>()).Returns([Container("c1", "created", "slot-1")]);
        _runtime.InspectAsync("c1", Arg.Any<CancellationToken>()).Returns(new ContainerState("created", 0, null, null));
        _time.Advance(TimeSpan.FromMinutes(3));

        await Create().RunOnceAsync(TestContext.Current.CancellationToken);

        await _runtime.Received(1).RemoveAsync("c1", Arg.Any<CancellationToken>());
        await _api.Received(1).DeleteRunnerAsync(Sandbox, 7, Arg.Any<CancellationToken>());
        await _runtime.Received(1).CreateAsync(Arg.Is<ContainerSpec>(s => s.Cpuset == "0-4"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnce_ContainerJustCreated_KeepsItsSlot()
    {
        QueueJobs(1);
        _runtime.ListByLabelAsync("orchestrator.managed=true", Arg.Any<CancellationToken>()).Returns([Container("c1", "created", "slot-1")]);
        _time.Advance(TimeSpan.FromSeconds(30));

        await Create().RunOnceAsync(TestContext.Current.CancellationToken);

        await _runtime.DidNotReceiveWithAnyArgs().RemoveAsync(default!, TestContext.Current.CancellationToken);
    }

    private void QueueJobs(int count)
    {
        _api.ListRunIdsAsync(Sandbox, "queued", Arg.Any<CancellationToken>()).Returns([9L]);
        _api.ListJobsAsync(Sandbox, 9, Arg.Any<CancellationToken>())
            .Returns([.. Enumerable.Range(1, count).Select(i => new WorkflowJob(i, 9, "queued", ["vm-pool"], null))]);
    }

    private static ContainerSummary Container(string id, string state, string slot) => new(id, $"orch-{id}", state, new Dictionary<string, string>
    {
        ["orchestrator.managed"] = "true",
        ["orchestrator.repo"] = "thiagoluga/runner-pool-sandbox",
        ["orchestrator.slot"] = slot,
        ["orchestrator.runner-name"] = $"orch-{id}",
        ["orchestrator.runner-id"] = "7",
        ["orchestrator.created-at"] = "2026-11-15T12:00:00.0000000+00:00",
    });

    private PoolCycle Create()
    {
        var options = TestOptions.Wrap(TestOptions.Orchestrator());
        var time = _time;
        return new PoolCycle(
            _runtime,
            _api,
            new DemandPoller(_api, options),
            new RepositoryGuard(_api, options, new ListLogger<RepositoryGuard>()),
            new RunnerLauncher(_api, _runtime, options, time, new ListLogger<RunnerLauncher>()),
            options,
            TestOptions.Wrap(TestOptions.Repositories("thiagoluga/runner-pool-sandbox")),
            time,
            _logger);
    }
}
