using System.Net;
using System.Text.Json;
using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.Pool;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests.GitHub;

public sealed class GitHubRunnerApiTests : IDisposable
{
    private const string Token = "ghs_installationtoken";
    private static readonly RepositoryName Sandbox = new("thiagoluga", "runner-pool-sandbox");
    private readonly FakeHttpHandler _handler = new();
    private readonly IGitHubCredentialSource _credentials = Substitute.For<IGitHubCredentialSource>();

    public GitHubRunnerApiTests()
    {
        _credentials.GetInstallationTokenAsync(Arg.Any<CancellationToken>()).Returns(Token);
    }

    [Fact]
    public async Task ListRunIds_SecondCallAnswered304_ReusesCachedBodyAndSendsETag()
    {
        var calls = 0;
        _handler.On(HttpMethod.Get, "/repos/thiagoluga/runner-pool-sandbox/actions/runs?status=queued", _ => ++calls == 1
            ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"total_count":1,"workflow_runs":[{"id":7,"status":"queued"}]}""", new Dictionary<string, string> { ["ETag"] = "\"abc\"" })
            : new HttpResponseMessage(HttpStatusCode.NotModified));
        var api = Create();

        var first = await api.ListRunIdsAsync(Sandbox, "queued", TestContext.Current.CancellationToken);
        var second = await api.ListRunIdsAsync(Sandbox, "queued", TestContext.Current.CancellationToken);

        first.Should().Equal(7);
        second.Should().Equal(7);
        _handler.Requests[0].IfNoneMatch.Should().BeEmpty();
        _handler.Requests[1].IfNoneMatch.Should().Be("\"abc\"");
        _handler.Requests.Should().OnlyContain(r => r.Authorization == $"Bearer {Token}");
    }

    [Fact]
    public async Task ListJobs_ReadsStatusLabelsAndRateLimit()
    {
        _handler.On(HttpMethod.Get, "/repos/thiagoluga/runner-pool-sandbox/actions/runs/7/jobs?filter=latest", HttpStatusCode.OK,
            """{"total_count":2,"jobs":[{"id":1,"run_id":7,"status":"queued","labels":["vm-pool"],"runner_name":null,"created_at":"2026-11-15T12:00:00Z"},{"id":2,"run_id":7,"status":"completed","labels":["ubuntu-24.04"]}]}""",
            new Dictionary<string, string> { ["x-ratelimit-limit"] = "5000", ["x-ratelimit-remaining"] = "400", ["x-ratelimit-reset"] = "1794000000" });
        var api = Create();

        var jobs = await api.ListJobsAsync(Sandbox, 7, TestContext.Current.CancellationToken);

        jobs.Should().HaveCount(2);
        jobs[0].Should().BeEquivalentTo(new { Id = 1L, RunId = 7L, Status = "queued", Labels = new[] { "vm-pool" } });
        api.RateLimit.Should().Be(new RateLimitStatus(5000, 400, DateTimeOffset.FromUnixTimeSeconds(1794000000)));
        api.RateLimit!.IsLow.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateJitConfig_PostsNameLabelsGroupAndWorkFolder_AndHidesConfigInToString()
    {
        _handler.On(HttpMethod.Post, "/repos/thiagoluga/runner-pool-sandbox/actions/runners/generate-jitconfig", HttpStatusCode.Created,
            """{"runner":{"id":23,"name":"orch-x","status":"offline","busy":false},"encoded_jit_config":"SECRETJITCONFIG"}""");
        var api = Create();

        var runner = await api.GenerateJitConfigAsync(Sandbox, "orch-x", ["self-hosted", "vm-pool"], TestContext.Current.CancellationToken);

        runner.RunnerId.Should().Be(23);
        runner.EncodedJitConfig.Should().Be("SECRETJITCONFIG");
        runner.ToString().Should().NotContain("SECRETJITCONFIG");
        using var body = JsonDocument.Parse(_handler.Requests.Single().Body!);
        body.RootElement.GetProperty("name").GetString().Should().Be("orch-x");
        body.RootElement.GetProperty("runner_group_id").GetInt64().Should().Be(1);
        body.RootElement.GetProperty("work_folder").GetString().Should().Be("_work");
        body.RootElement.GetProperty("labels").EnumerateArray().Select(e => e.GetString()).Should().Equal("self-hosted", "vm-pool");
    }

    [Fact]
    public async Task ListJobs_FullFirstPage_ReadsTheNextPage()
    {
        var firstPage = string.Join(',', Enumerable.Range(1, 100).Select(i => $$"""{"id":{{i}},"run_id":7,"status":"queued","labels":["vm-pool"]}"""));
        _handler
            .On(HttpMethod.Get, "/repos/thiagoluga/runner-pool-sandbox/actions/runs/7/jobs?filter=latest&per_page=100&page=2", HttpStatusCode.OK,
                """{"jobs":[{"id":101,"run_id":7,"status":"queued","labels":["vm-pool"]}]}""")
            .On(HttpMethod.Get, "/repos/thiagoluga/runner-pool-sandbox/actions/runs/7/jobs?filter=latest&per_page=100", HttpStatusCode.OK,
                $$"""{"jobs":[{{firstPage}}]}""");
        var api = Create();

        var jobs = await api.ListJobsAsync(Sandbox, 7, TestContext.Current.CancellationToken);

        jobs.Should().HaveCount(101);
        _handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteRunner_AlreadyGone_IsNotAnError()
    {
        _handler.On(HttpMethod.Delete, "/repos/thiagoluga/runner-pool-sandbox/actions/runners/23", HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        var api = Create();

        var act = () => api.DeleteRunnerAsync(Sandbox, 23, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetRepository_Forbidden_ThrowsWithGitHubMessageAndNoToken()
    {
        _handler.On(HttpMethod.Get, "/repos/thiagoluga/runner-pool-sandbox", HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");
        var api = Create();

        var act = () => api.GetRepositoryAsync(Sandbox, TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<GitHubApiException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        thrown.Which.Message.Should().Contain("403").And.Contain("Resource not accessible by integration").And.NotContain(Token);
    }

    [Fact]
    public async Task GetRepository_ReadsVisibilityAndOwner()
    {
        _handler.On(HttpMethod.Get, "/repos/thiagoluga/runner-pool-sandbox", HttpStatusCode.OK,
            """{"full_name":"thiagoluga/runner-pool-sandbox","private":true,"owner":{"login":"thiagoluga"}}""");
        var api = Create();

        var info = await api.GetRepositoryAsync(Sandbox, TestContext.Current.CancellationToken);

        info.Should().Be(new RepositoryInfo(Sandbox, true, "thiagoluga"));
    }

    [Fact]
    public async Task ListRunners_ReadsBusyFlag()
    {
        _handler.On(HttpMethod.Get, "/repos/thiagoluga/runner-pool-sandbox/actions/runners", HttpStatusCode.OK,
            """{"total_count":1,"runners":[{"id":23,"name":"orch-x","os":"Linux","status":"online","busy":true,"labels":[]}]}""");
        var api = Create();

        var runners = await api.ListRunnersAsync(Sandbox, TestContext.Current.CancellationToken);

        runners.Should().Equal(new SelfHostedRunner(23, "orch-x", "online", true));
    }

    private GitHubRunnerApi Create() => new(GitHubHttp.CreateClient(new Uri("https://api.github.test/"), _handler), _credentials);

    public void Dispose() => _handler.Dispose();
}
