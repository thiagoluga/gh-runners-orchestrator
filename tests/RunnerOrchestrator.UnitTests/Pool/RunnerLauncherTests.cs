using Microsoft.Extensions.Time.Testing;
using RunnerOrchestrator.Docker;
using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.Pool;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests.Pool;

public sealed class RunnerLauncherTests
{
    private const string JitConfig = "eyJTRUNSRVQiOiJKSVQifQ==SECRETJIT";
    private static readonly RepositoryName Sandbox = new("thiagoluga", "runner-pool-sandbox");
    private readonly IGitHubRunnerApi _api = Substitute.For<IGitHubRunnerApi>();
    private readonly IContainerRuntime _runtime = Substitute.For<IContainerRuntime>();
    private readonly ListLogger<RunnerLauncher> _logger = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

    public RunnerLauncherTests()
    {
        _api.GenerateJitConfigAsync(Sandbox, Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => new JitRunner(23, call.ArgAt<string>(1), JitConfig));
        _runtime.CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>()).Returns("c1");
    }

    [Fact]
    public async Task Launch_CreatesContainerWithSlotLimitsLabelsAndJitOnlyInEnvironment()
    {
        var launcher = Create();
        var options = TestOptions.Orchestrator();

        await launcher.LaunchAsync(Sandbox, options.Slots[1], TestContext.Current.CancellationToken);

        await _runtime.Received(1).EnsureImageAsync(options.RunnerImage, Arg.Any<CancellationToken>());
        await _api.Received(1).GenerateJitConfigAsync(
            Sandbox,
            Arg.Is<string>(n => n.StartsWith("orch-thiagoluga-runner-pool-sandbox-slot-2-", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "self-hosted", "linux", "x64", "vm-pool" })),
            Arg.Any<CancellationToken>());
        var spec = (ContainerSpec)_runtime.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IContainerRuntime.CreateAsync)).GetArguments()[0]!;
        spec.Cpuset.Should().Be("5-9");
        spec.MemoryBytes.Should().Be(16L * 1024 * 1024 * 1024);
        spec.NofileLimit.Should().Be(65536);
        spec.DockerSocketGroupId.Should().Be(988);
        spec.Environment.Should().Equal(new Dictionary<string, string> { ["ACTIONS_RUNNER_INPUT_JITCONFIG"] = JitConfig });
        spec.Labels.Should().Contain("orchestrator.managed", "true")
            .And.Contain("orchestrator.repo", "thiagoluga/runner-pool-sandbox")
            .And.Contain("orchestrator.slot", "slot-2")
            .And.Contain("orchestrator.runner-id", "23")
            .And.Contain("orchestrator.created-at", "2026-11-15T12:00:00.0000000+00:00");
        spec.Labels.Values.Should().NotContain(v => v.Contains("SECRET", StringComparison.Ordinal));
        await _runtime.Received(1).StartAsync("c1", Arg.Any<CancellationToken>());
        _logger.AllText.Should().Contain("started for thiagoluga/runner-pool-sandbox in slot-2").And.NotContain("SECRET");
    }

    [Fact]
    public async Task Launch_StartFails_RemovesContainerDeletesRunnerAndLogsNoSecret()
    {
        _runtime.StartAsync("c1", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new DockerEngineException("Docker answered 500 to POST /v1.47/containers/c1/start: boom"));
        var launcher = Create();

        var act = () => launcher.LaunchAsync(Sandbox, TestOptions.Orchestrator().Slots[0], TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DockerEngineException>();
        await _runtime.Received(1).RemoveAsync("c1", Arg.Any<CancellationToken>());
        await _api.Received(1).DeleteRunnerAsync(Sandbox, 23, Arg.Any<CancellationToken>());
        _logger.AllText.Should().Contain("could not start").And.Contain("boom").And.NotContain("SECRET");
    }

    [Fact]
    public async Task Launch_JitConfigFails_CreatesNothing()
    {
        _api.GenerateJitConfigAsync(Sandbox, Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<JitRunner>(_ => throw new GitHubApiException("denied"));
        var launcher = Create();

        var act = () => launcher.LaunchAsync(Sandbox, TestOptions.Orchestrator().Slots[0], TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GitHubApiException>();
        await _runtime.DidNotReceiveWithAnyArgs().CreateAsync(default!, TestContext.Current.CancellationToken);
    }

    private RunnerLauncher Create() => new(_api, _runtime, TestOptions.Wrap(TestOptions.Orchestrator()), _time, _logger);
}
