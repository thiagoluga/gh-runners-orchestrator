using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.UnitTests.Pool;

public sealed class PoolHostsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
    private readonly PoolHosts _hosts = new("github-runner", ["gh-runner-2", "github-runner"], TimeSpan.FromMinutes(1));

    [Theory]
    [InlineData("gh-runner-2", true)]
    [InlineData("a", true)]
    [InlineData("a1-b2", true)]
    [InlineData("", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("a--b", false)]
    [InlineData("Host", false)]
    [InlineData("a_b", false)]
    [InlineData("abcdefghijklmnopq", false)]
    public void IsValidName(string name, bool valid) => PoolHosts.IsValidName(name).Should().Be(valid);

    [Theory]
    [InlineData("orch-github-runner--thiagoluga-saas-foundry-slot-1-0a1b", "github-runner")]
    [InlineData("orch-gh-runner-2--maozinha-insurapp-slot-2-0a1b", "gh-runner-2")]
    [InlineData("orch-thiagoluga-saas-foundry-slot-1-0a1b", "gh-runner-2")]
    [InlineData("orch-retired--thiagoluga-saas-foundry-slot-1-0a1b", null)]
    [InlineData("vm-dev-1", null)]
    public void OwnerOf(string runner, string? owner) => _hosts.OwnerOf(runner).Should().Be(owner);

    [Fact]
    public void OwnerOf_HostNameThatPrefixesAnother_PicksTheExactHost()
    {
        var hosts = new PoolHosts("a", ["a", "a-b"], TimeSpan.FromMinutes(1));

        hosts.OwnerOf("orch-a-b--o-r-slot-1-00").Should().Be("a-b");
        hosts.OwnerOf("orch-a--o-r-slot-1-00").Should().Be("a");
    }

    [Fact]
    public void OwnAndForeign()
    {
        _hosts.IsOwn("orch-github-runner--o-r-slot-1-00").Should().BeTrue();
        _hosts.IsForeign("orch-github-runner--o-r-slot-1-00").Should().BeFalse();
        _hosts.IsForeign("orch-gh-runner-2--o-r-slot-1-00").Should().BeTrue();
        _hosts.IsForeign("orch-retired--o-r-slot-1-00").Should().BeFalse();
        _hosts.IsOwn("orch-retired--o-r-slot-1-00").Should().BeFalse();
    }

    [Fact]
    public void SingleHost_OwnsEveryOrchRunnerAndServesEveryJob()
    {
        var single = new PoolHosts(string.Empty, [], TimeSpan.FromMinutes(1));

        single.IsMultiHost.Should().BeFalse();
        single.NamePrefix.Should().Be("orch-");
        single.IsOwn("orch-thiagoluga-x-slot-1-00").Should().BeTrue();
        single.IsForeign("orch-thiagoluga-x-slot-1-00").Should().BeFalse();
        single.MayServe(new WorkflowJob(11, 1, "queued", [], Now), Now, Now).Should().BeTrue();
    }

    [Fact]
    public void JobOwnerIsIdModuloHostCount()
    {
        _hosts.OwnerOfJob(10).Should().Be("gh-runner-2");
        _hosts.OwnerOfJob(11).Should().Be("github-runner");
        _hosts.NamePrefix.Should().Be("orch-github-runner--");
    }

    [Theory]
    [InlineData(11, 0, true)]
    [InlineData(10, 59, false)]
    [InlineData(10, 60, true)]
    public void MayServe(long jobId, int secondsSeen, bool expected) =>
        _hosts.MayServe(new WorkflowJob(jobId, 1, "queued", [], null), Now - TimeSpan.FromSeconds(secondsSeen), Now).Should().Be(expected);

    [Fact]
    public void MayServe_IgnoresGitHubsCreationTime()
    {
        // A job that waited for a `needs` keeps its run's creation time.
        var job = new WorkflowJob(10, 1, "queued", [], Now - TimeSpan.FromMinutes(6));

        _hosts.MayServe(job, Now, Now).Should().BeFalse();
    }
}
