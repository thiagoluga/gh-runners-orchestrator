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
    public async Task CountQueuedJobs_ReadsQueuedAndInProgressRunsOnce_CountsOnlyServedQueuedJobs()
    {
        _api.ListRunIdsAsync(Sandbox, "queued", Arg.Any<CancellationToken>()).Returns([1L]);
        _api.ListRunIdsAsync(Sandbox, "in_progress", Arg.Any<CancellationToken>()).Returns([1L, 2L]);
        _api.ListJobsAsync(Sandbox, 1, Arg.Any<CancellationToken>()).Returns([
            Job(1, "queued", "vm-pool"),
            Job(1, "queued", "ubuntu-24.04"),
        ]);
        _api.ListJobsAsync(Sandbox, 2, Arg.Any<CancellationToken>()).Returns([
            Job(2, "in_progress", "vm-pool"),
            Job(2, "queued", "self-hosted", "vm-pool"),
        ]);
        var poller = new DemandPoller(_api, TestOptions.Wrap(TestOptions.Orchestrator()));

        var count = await poller.CountQueuedJobsAsync(Sandbox, TestContext.Current.CancellationToken);

        count.Should().Be(2);
        await _api.Received(1).ListJobsAsync(Sandbox, 1, Arg.Any<CancellationToken>());
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
        logger.AllText.Should().Contain("not owned by thiagoluga");
        await _api.DidNotReceiveWithAnyArgs().GetRepositoryAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_RepositoryNotCoveredByInstallation_IsRefused()
    {
        _api.GetRepositoryAsync(Sandbox, Arg.Any<CancellationToken>()).Returns<RepositoryInfo>(_ => throw new GitHubApiException(HttpStatusCode.NotFound, "GET", "/repos/x", "Not Found"));
        var (guard, logger) = CreateGuard();

        var enabled = await guard.FilterAsync([new RepositoryOptions { Name = "thiagoluga/runner-pool-sandbox" }], TestContext.Current.CancellationToken);

        enabled.Should().BeEmpty();
        logger.AllText.Should().Contain("not readable by the App installation (NotFound)");
    }

    [Fact]
    public async Task Filter_TransferredToAnotherOwner_IsRefused()
    {
        _api.GetRepositoryAsync(Sandbox, Arg.Any<CancellationToken>()).Returns(new RepositoryInfo(Sandbox, true, "someone"));
        var (guard, _) = CreateGuard();

        var enabled = await guard.FilterAsync([new RepositoryOptions { Name = "thiagoluga/runner-pool-sandbox" }], TestContext.Current.CancellationToken);

        enabled.Should().BeEmpty();
    }

    private (RepositoryGuard Guard, ListLogger<RepositoryGuard> Logger) CreateGuard()
    {
        var logger = new ListLogger<RepositoryGuard>();
        return (new RepositoryGuard(_api, TestOptions.Wrap(TestOptions.Orchestrator()), logger), logger);
    }

    private static WorkflowJob Job(long runId, string status, params string[] labels) => new(runId * 10, runId, status, labels, null);
}
