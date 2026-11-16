using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.Docker;
using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.Pool;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests.GitHub;

public sealed class InstallationRouterTests : IDisposable
{
    private readonly string _keyPath = Path.Join(Path.GetTempPath(), $"router-test-{Guid.NewGuid():N}.pem");
    private readonly FakeHttpHandler _handler = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly MutableOptionsMonitor<RepositoryListOptions> _repositories = TestOptions.Monitor(TestOptions.Repositories("thiagoluga/shared", "maozinha/shared"));
    private readonly OrchestratorOptions _options;
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
    private HttpStatusCode _maozinhaStatus = HttpStatusCode.OK;
    private bool _lowBudget;
    private bool _public;
    private bool _revoked;
    private bool _missing;
    private bool _demandFailure;
    private string? _installationOverride;

    public InstallationRouterTests()
    {
        using var key = RSA.Create(2048);
        File.WriteAllText(_keyPath, key.ExportRSAPrivateKeyPem());
        _options = TestOptions.Orchestrator(_keyPath);
        _options.Owner = string.Empty;
        _options.Accounts.Add(new AccountOptions { Owner = "thiagoluga", InstallationId = 42 });
        _options.Accounts.Add(new AccountOptions { Owner = "maozinha", InstallationId = 84 });
        AddAccount("thiagoluga", 42);
        AddAccount("maozinha", 84);
        _handler.On(HttpMethod.Get, "/installation/repositories", request =>
        {
            var owner = request.Headers.Authorization!.Parameter!.Split('-')[1];
            var name = _missing && owner == "maozinha" ? "different" : "shared";
            return FakeHttpHandler.Json(HttpStatusCode.OK, RepositoryList(owner, name, !(_public && owner == "maozinha")));
        });
        _handler.On(HttpMethod.Delete, "/installation/token", HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Requests_HomonymousRepositories_UseTheirOwnTokensAndInstallations()
    {
        using var router = Create();
        var thiago = await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
        var mao = await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);

        thiago.Name.Should().Be(new RepositoryName("thiagoluga", "shared"));
        mao.Name.Should().Be(new RepositoryName("maozinha", "shared"));
        _handler.Requests.Where(r => r.Uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal))
            .Select(r => r.Authorization).Should().Equal("Bearer fake-thiagoluga-scoped-1", "Bearer fake-maozinha-scoped-1");
        router.GetInstallationId(thiago.Name).Should().Be(42);
        router.GetInstallationId(mao.Name).Should().Be(84);
        var scoped = _handler.Requests.Where(r => r.Method == HttpMethod.Post && r.Body!.Contains("repositories", StringComparison.Ordinal));
        var requestedRepositories = scoped.Select(request => RequestedRepositoryNames(request.Body!)).ToList();
        requestedRepositories.Should().HaveCount(2);
        requestedRepositories.Should().OnlyContain(names => names.SequenceEqual(new[] { "shared" }));
    }

    [Theory]
    [InlineData("{\"id\":42,\"app_id\":12345,\"account\":{\"login\":\"maozinha\"}}")]
    [InlineData("{\"id\":84,\"app_id\":999,\"account\":{\"login\":\"maozinha\"}}")]
    [InlineData("{\"id\":84,\"app_id\":12345,\"account\":{\"login\":\"thiagoluga\"}}")]
    public async Task Requests_InconsistentInstallation_RefuseWithoutMintingOrFallingBack(string installation)
    {
        _installationOverride = installation;
        using var router = Create();
        var act = () => router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GitHubApiException>();
        _handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Requests_UnauthorizedOwner_NeverSendsARequest()
    {
        using var router = Create();
        var act = () => router.GetRepositoryAsync(new("someone", "shared"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<GitHubApiException>();
        _handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Requests_PublicOrUncoveredRepository_RefusedWithoutBlockingHealthyAccount(bool isPublic)
    {
        _public = isPublic;
        _missing = !isPublic;
        using var router = Create();
        var act = () => router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<GitHubApiException>();
        _handler.Requests.Should().NotContain(r => r.Uri.AbsolutePath == "/repos/maozinha/shared");
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Requests_MissingRepository_DoesNotBackOffOtherRepositoriesInSameInstallation()
    {
        using var router = Create();
        var act = () => router.GetRepositoryAsync(new("thiagoluga", "missing"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<GitHubApiException>();
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Renewal_IsPerAccount_RevokesOnlyItsReplacedToken()
    {
        using var router = Create();
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(10));
        await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(46));
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
        await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);

        _versions["thiagoluga"].Should().Be(2);
        _versions["maozinha"].Should().Be(1);
        _handler.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => r.Authorization)
            .Should().Contain("Bearer fake-thiagoluga-scoped-1").And.NotContain("Bearer fake-maozinha-scoped-1");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RevokedToken_InvalidatesItsCache_AndDoesNotSuspendHealthyAccount(HttpStatusCode status)
    {
        using var router = Create();
        await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        _maozinhaStatus = status;
        var act = () => router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<GitHubApiException>();
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(21));
        _maozinhaStatus = HttpStatusCode.OK;
        await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);

        _versions["maozinha"].Should().Be(2);
        _handler.Requests.Last().IfNoneMatch.Should().BeEmpty();
    }

    [Fact]
    public async Task InstallationRemoved_RenewalFails_HealthyAccountStillWorks()
    {
        using var router = Create();
        await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(56));
        _revoked = true;
        var act = () => router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<GitHubApiException>();
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LowBudget_OnlyBacksOffItsAccount_AndResumesAfterReset()
    {
        _lowBudget = true;
        using var router = Create();
        await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        var before = _handler.Requests.Count;
        var act = () => router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<GitHubApiException>();
        _handler.Requests.Should().HaveCount(before);
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(2));
        _lowBudget = false;
        await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RepositoryListReload_ForOneOwner_DoesNotRenewAnotherOwnersToken()
    {
        using var router = Create();
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
        await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        _repositories.CurrentValue = TestOptions.Repositories("thiagoluga/shared");
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
        await router.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        _versions["thiagoluga"].Should().Be(1);
        _versions["maozinha"].Should().Be(2);
    }

    [Fact]
    public async Task LegacyOwner_StillDiscoversAndServesItsOriginalInstallation()
    {
        _options.Accounts.Clear();
        _options.Owner = "thiagoluga";
        using var router = Create();
        await router.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
        router.GetInstallationId(new("thiagoluga", "shared")).Should().Be(42);
        _handler.Requests.Should().NotContain(r => r.Uri.AbsolutePath.Contains("maozinha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GlobalCycle_TwoAccountsWithSameRepositoryName_ShareTwoSlotsAcrossRestart()
    {
        foreach (var entry in _repositories.CurrentValue.Repositories)
        {
            entry.MaxConcurrent = 1;
        }

        var runtime = Substitute.For<IContainerRuntime>();
        var containers = new List<ContainerSummary>();
        runtime.ListByLabelAsync(ManagedLabels.ManagedFilter, Arg.Any<CancellationToken>()).Returns(_ => containers.ToArray());
        runtime.CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var spec = call.Arg<ContainerSpec>();
            var id = $"c{containers.Count}";
            containers.Add(new ContainerSummary(id, spec.Name, "running", spec.Labels));
            return id;
        });
        using var router = Create();
        await Cycle(router, runtime).RunOnceAsync(TestContext.Current.CancellationToken);
        containers.Should().HaveCount(2);
        containers.Select(c => c.Labels[ManagedLabels.Repository]).Should().BeEquivalentTo(new[] { "thiagoluga/shared", "maozinha/shared" });
        containers.Select(c => c.Labels[ManagedLabels.Slot]).Should().OnlyHaveUniqueItems();
        containers.Select(c => c.Labels[ManagedLabels.InstallationId]).Should().BeEquivalentTo(new[] { "42", "84" });
        using var restarted = Create();
        await Cycle(restarted, runtime).RunOnceAsync(TestContext.Current.CancellationToken);
        await runtime.Received(2).CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GlobalCycle_OneAccountsDemandFails_StillLaunchesForTheOtherAccount()
    {
        _demandFailure = true;
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ListByLabelAsync(ManagedLabels.ManagedFilter, Arg.Any<CancellationToken>()).Returns([]);
        runtime.CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>()).Returns("created");
        using var router = Create();
        await Cycle(router, runtime).RunOnceAsync(TestContext.Current.CancellationToken);
        await runtime.Received(2).CreateAsync(Arg.Is<ContainerSpec>(s => s.Labels[ManagedLabels.Repository] == "thiagoluga/shared"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("999")]
    [InlineData(null)]
    public async Task GlobalCycle_AmbiguousInstallation_KeepsContainerAndSlotWithoutDeletingRegistration(string? installation)
    {
        var labels = new Dictionary<string, string>
        {
            [ManagedLabels.Managed] = "true",
            [ManagedLabels.Repository] = "maozinha/shared",
            [ManagedLabels.Slot] = "slot-1",
            [ManagedLabels.RunnerName] = "orch-existing",
            [ManagedLabels.RunnerId] = "7",
        };
        if (installation is not null)
        {
            labels[ManagedLabels.InstallationId] = installation;
        }

        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ListByLabelAsync(ManagedLabels.ManagedFilter, Arg.Any<CancellationToken>())
            .Returns([new ContainerSummary("existing", "orch-existing", "exited", labels)]);
        runtime.CreateAsync(Arg.Any<ContainerSpec>(), Arg.Any<CancellationToken>()).Returns("created");
        using var router = Create();
        await Cycle(router, runtime).RunOnceAsync(TestContext.Current.CancellationToken);
        await runtime.DidNotReceiveWithAnyArgs().RemoveAsync(default!, TestContext.Current.CancellationToken);
        await runtime.Received(1).CreateAsync(Arg.Is<ContainerSpec>(s => s.Labels[ManagedLabels.Slot] == "slot-2"), Arg.Any<CancellationToken>());
        _handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleanup_AfterRepositoryRemovalAndRestart_StaysQualifiedToItsInstallation()
    {
        using (var before = Create())
        {
            await before.GetRepositoryAsync(new("thiagoluga", "shared"), TestContext.Current.CancellationToken);
            await before.GetRepositoryAsync(new("maozinha", "shared"), TestContext.Current.CancellationToken);
        }

        _repositories.CurrentValue = TestOptions.Repositories();
        using var restarted = Create();
        var cleanup = await restarted.ListCleanupRepositoriesAsync(TestContext.Current.CancellationToken);
        cleanup.Should().BeEquivalentTo(new[] { new RepositoryName("thiagoluga", "shared"), new RepositoryName("maozinha", "shared") });
        _revoked = true;
        _time.Advance(TimeSpan.FromMinutes(56));
        cleanup = await restarted.ListCleanupRepositoriesAsync(TestContext.Current.CancellationToken);
        cleanup.Should().Equal(new RepositoryName("thiagoluga", "shared"));
    }

    [Fact]
    public async Task Launch_RepositoryRemovedAfterGuard_CannotObtainJitConfiguration()
    {
        using var router = Create();
        var repository = new RepositoryName("maozinha", "shared");
        await router.GetRepositoryAsync(repository, TestContext.Current.CancellationToken);
        _repositories.CurrentValue = TestOptions.Repositories("thiagoluga/shared");
        var act = () => router.GenerateJitConfigAsync(repository, "orch-new", ["vm-pool"], TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<GitHubApiException>();
        _handler.Requests.Should().NotContain(r => r.Uri.AbsolutePath.EndsWith("generate-jitconfig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleanup_SameNumericRunnerIdInTwoAccounts_UsesTheMatchingInstallationToken()
    {
        _handler.On(HttpMethod.Delete, "/repos/thiagoluga/shared/actions/runners/7", HttpStatusCode.NoContent);
        _handler.On(HttpMethod.Delete, "/repos/maozinha/shared/actions/runners/7", HttpStatusCode.NoContent);
        using var router = Create();
        await router.DeleteRunnerAsync(new("thiagoluga", "shared"), 7, TestContext.Current.CancellationToken);
        await router.DeleteRunnerAsync(new("maozinha", "shared"), 7, TestContext.Current.CancellationToken);
        _handler.Requests.Where(r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal))
            .Select(r => (r.Uri.AbsolutePath, r.Authorization)).Should().Equal(
                ("/repos/thiagoluga/shared/actions/runners/7", "Bearer fake-thiagoluga-scoped-1"),
                ("/repos/maozinha/shared/actions/runners/7", "Bearer fake-maozinha-scoped-1"));
    }

    private PoolCycle Cycle(IGitHubRunnerApi api, IContainerRuntime runtime)
    {
        var options = TestOptions.Wrap(_options);
        return new PoolCycle(new PoolBoundaries(runtime, api, _time), new DemandPoller(api, options),
            new RepositoryGuard(api, options, new ListLogger<RepositoryGuard>()),
            new RunnerLauncher(api, runtime, options, _time, new ListLogger<RunnerLauncher>()),
            options, _repositories, new ListLogger<PoolCycle>());
    }

    private void AddAccount(string owner, long id)
    {
        _handler.On(HttpMethod.Get, $"/users/{owner}/installation", _ =>
            FakeHttpHandler.Json(_revoked && owner == "maozinha" ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                owner == "maozinha" && _installationOverride is not null ? _installationOverride
                    : $$$"""{"id":{{{id}}},"app_id":12345,"account":{"login":"{{{owner}}}"}}"""));
        _handler.On(HttpMethod.Post, $"/app/installations/{id}/access_tokens", request =>
        {
            var scoped = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("repositories", StringComparison.Ordinal);
            if (scoped)
            {
                _versions[owner] = _versions.GetValueOrDefault(owner) + 1;
            }

            var kind = scoped ? $"scoped-{_versions[owner]}" : "metadata";
            return FakeHttpHandler.Json(HttpStatusCode.Created,
                $$$"""{"token":"fake-{{{owner}}}-{{{kind}}}","expires_at":"{{{_time.GetUtcNow().AddHours(1):O}}}"}""");
        });
        _handler.On(HttpMethod.Get, $"/repos/{owner}/shared/actions/runs/9/jobs", HttpStatusCode.OK,
            """{"jobs":[{"id":1,"run_id":9,"status":"queued","labels":["vm-pool"]},{"id":2,"run_id":9,"status":"queued","labels":["vm-pool"]},{"id":3,"run_id":9,"status":"queued","labels":["vm-pool"]}]}""");
        _handler.On(HttpMethod.Get, $"/repos/{owner}/shared/actions/runs", request =>
            FakeHttpHandler.Json(_demandFailure && owner == "maozinha" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
                request.RequestUri!.Query.Contains("status=queued", StringComparison.Ordinal)
                    ? """{"workflow_runs":[{"id":9}]}""" : """{"workflow_runs":[]}"""));
        _handler.On(HttpMethod.Post, $"/repos/{owner}/shared/actions/runners/generate-jitconfig", request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return FakeHttpHandler.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new
            {
                runner = new { id = 7, name = body.RootElement.GetProperty("name").GetString() },
                encoded_jit_config = "fake-jit-config",
            }));
        });
        _handler.On(HttpMethod.Get, $"/repos/{owner}/shared/actions/runners", HttpStatusCode.OK, """{"runners":[]}""");
        _handler.On(HttpMethod.Get, $"/repos/{owner}/shared", _ => FakeHttpHandler.Json(
            owner == "maozinha" ? _maozinhaStatus : HttpStatusCode.OK,
            $$$"""{"full_name":"{{{owner}}}/shared","private":true,"owner":{"login":"{{{owner}}}"}}""",
            new Dictionary<string, string>
            {
                ["ETag"] = "\"same-etag\"",
                ["x-ratelimit-limit"] = "5000",
                ["x-ratelimit-remaining"] = _lowBudget && owner == "maozinha" ? "1" : "4999",
                ["x-ratelimit-reset"] = _time.GetUtcNow().AddMinutes(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
    }

    private static string?[] RequestedRepositoryNames(string requestBody)
    {
        using var body = JsonDocument.Parse(requestBody);
        return body.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()).ToArray();
    }

    private static string RepositoryList(string owner, string name, bool isPrivate) =>
        $$$"""{"repositories":[{"full_name":"{{{owner}}}/{{{name}}}","private":{{{isPrivate.ToString().ToLowerInvariant()}}},"owner":{"login":"{{{owner}}}"}}]}""";

    private InstallationRouter Create() => new(GitHubHttp.CreateClient(new Uri("https://api.github.test/"), _handler),
        TestOptions.Wrap(_options), _repositories, _time, NullLoggerFactory.Instance);

    public void Dispose()
    {
        File.Delete(_keyPath);
        _handler.Dispose();
    }
}
