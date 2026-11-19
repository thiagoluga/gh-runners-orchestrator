using System.Net;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.Pool;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests.Pool;

public sealed class DemandAndGuardTests
{
    private static readonly RepositoryName Sandbox = new("thiagoluga", "runner-pool-sandbox");
    private readonly IGitHubRunnerApi _api = Substitute.For<IGitHubRunnerApi>();

    [Fact]
    public async Task ReadQueuedJobs_ReadsQueuedAndInProgressRunsOnce_CountsOnlyServedQueuedJobsAndOldest()
    {
        _api.ListRunIdsAsync(Sandbox, "queued", Arg.Any<CancellationToken>()).Returns([1L]);
        _api.ListRunIdsAsync(Sandbox, "in_progress", Arg.Any<CancellationToken>()).Returns([1L, 2L]);
        _api.ListJobsAsync(Sandbox, 1, Arg.Any<CancellationToken>()).Returns([
            Job(1, "queued", "vm-pool"),
            Job(1, "queued", "ubuntu-24.04"),
        ]);
        _api.ListJobsAsync(Sandbox, 2, Arg.Any<CancellationToken>()).Returns([
            Job(2, "in_progress", "vm-pool"),
            Job(2, "queued", "self-hosted", "vm-pool") with { CreatedAt = new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.Zero) },
        ]);
        var poller = new DemandPoller(_api, TestOptions.Wrap(TestOptions.Orchestrator()));

        var queued = await poller.ReadQueuedJobsAsync(Sandbox, TestContext.Current.CancellationToken);

        queued.Should().Be(new QueuedJobs(2, new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.Zero)));
        await _api.Received(1).ListJobsAsync(Sandbox, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadQueuedJobs_PendingRunCountsReadyJobsButNotConcurrencyBlockedJobs()
    {
        _api.ListRunIdsAsync(Sandbox, "queued", Arg.Any<CancellationToken>()).Returns([1L]);
        _api.ListRunIdsAsync(Sandbox, "pending", Arg.Any<CancellationToken>()).Returns([1L, 2L]);
        _api.ListJobsAsync(Sandbox, 1, Arg.Any<CancellationToken>()).Returns([
            Job(1, "queued", "vm-pool"),
        ]);
        var oldest = new DateTimeOffset(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);
        _api.ListJobsAsync(Sandbox, 2, Arg.Any<CancellationToken>()).Returns([
            Job(2, "queued", "vm-pool") with { CreatedAt = oldest },
            Job(2, "pending", "vm-pool"),
            Job(2, "queued", "ubuntu-latest"),
        ]);
        var poller = new DemandPoller(_api, TestOptions.Wrap(TestOptions.Orchestrator()));

        var queued = await poller.ReadQueuedJobsAsync(Sandbox, TestContext.Current.CancellationToken);

        queued.Should().Be(new QueuedJobs(2, oldest));
        await _api.Received(1).ListJobsAsync(Sandbox, 1, Arg.Any<CancellationToken>());
        await _api.Received(1).ListJobsAsync(Sandbox, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Filter_PrivateRepositoryOfOwner_IsEnabled()
    {
        _api.GetRepositoryAsync(Sandbox, Arg.Any<CancellationToken>()).Returns(new RepositoryInfo(Sandbox, true, "thiagoluga"));
        var (guard, _) = CreateGuard();

        var enabled = await guard.FilterAsync([new RepositoryOptions { Name = "thiagoluga/runner-pool-sandbox", MaxConcurrent = 2 }], TestContext.Current.CancellationToken);

        enabled.Should().Equal(new EnabledRepository(Sandbox, 2));
    }

    [Fact]
    public async Task Filter_PublicRepository_IsRefusedAndReportedOnce()
    {
        _api.GetRepositoryAsync(Sandbox, Arg.Any<CancellationToken>()).Returns(new RepositoryInfo(Sandbox, false, "thiagoluga"));
        var (guard, logger) = CreateGuard();
        RepositoryOptions[] configured = [new RepositoryOptions { Name = "thiagoluga/runner-pool-sandbox" }];

        (await guard.FilterAsync(configured, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await guard.FilterAsync(configured, TestContext.Current.CancellationToken)).Should().BeEmpty();

        logger.Entries.Should().ContainSingle().Which.Should().Contain("public");
    }

    [Fact]
    public async Task Filter_AnotherOwnerInConfiguration_IsRefusedWithoutCallingGitHub()
    {
        var (guard, logger) = CreateGuard();

        var enabled = await guard.FilterAsync([new RepositoryOptions { Name = "someone/else" }], TestContext.Current.CancellationToken);

        enabled.Should().BeEmpty();
        logger.AllText.Should().Contain("owner is not authorized");
        await _api.DidNotReceiveWithAnyArgs().GetRepositoryAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_RepositoryNotCoveredByInstallation_IsRefused()
    {
        _api.GetRepositoryAsync(Sandbox, Arg.Any<CancellationToken>()).Returns<RepositoryInfo>(_ => throw new GitHubApiException(HttpStatusCode.NotFound, "GET", "/repos/x", "Not Found"));
        var (guard, logger) = CreateGuard();

        var enabled = await guard.FilterAsync([new RepositoryOptions { Name = "thiagoluga/runner-pool-sandbox" }], TestContext.Current.CancellationToken);

        enabled.Should().BeEmpty();
        logger.AllText.Should().Contain("not readable by the App installation");
    }

    [Fact]
    public async Task Filter_TransferredToAnotherOwner_IsRefused()
    {
        _api.GetRepositoryAsync(Sandbox, Arg.Any<CancellationToken>()).Returns(new RepositoryInfo(Sandbox, true, "someone"));
        var (guard, _) = CreateGuard();

        var enabled = await guard.FilterAsync([new RepositoryOptions { Name = "thiagoluga/runner-pool-sandbox" }], TestContext.Current.CancellationToken);

        enabled.Should().BeEmpty();
    }

    [Fact]
    public async Task Filter_DifferentRepositoryReturnedByApi_IsRefused()
    {
        _api.GetRepositoryAsync(Sandbox, Arg.Any<CancellationToken>()).Returns(new RepositoryInfo(new("thiagoluga", "other"), true, "thiagoluga"));
        var (guard, _) = CreateGuard();
        (await guard.FilterAsync([new RepositoryOptions { Name = Sandbox.ToString() }], TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Filter_DuplicatedRepositoryOrNegativeCap_IsRefused()
    {
        _api.GetRepositoryAsync(Sandbox, Arg.Any<CancellationToken>()).Returns(new RepositoryInfo(Sandbox, true, "thiagoluga"));
        var (guard, _) = CreateGuard();
        (await guard.FilterAsync([
            new RepositoryOptions { Name = Sandbox.ToString(), MaxConcurrent = 1 },
            new RepositoryOptions { Name = Sandbox.ToString().ToUpperInvariant(), MaxConcurrent = 2 },
        ], TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await guard.FilterAsync([new RepositoryOptions { Name = Sandbox.ToString(), MaxConcurrent = -1 }], TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    private (RepositoryGuard Guard, ListLogger<RepositoryGuard> Logger) CreateGuard()
    {
        var logger = new ListLogger<RepositoryGuard>();
        return (new RepositoryGuard(_api, TestOptions.Wrap(TestOptions.Orchestrator()), logger), logger);
    }

    private static WorkflowJob Job(long runId, string status, params string[] labels) => new(runId * 10, runId, status, labels, null);
}
