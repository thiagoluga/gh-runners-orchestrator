using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests.GitHub;

public sealed class GitHubAppCredentialsTests : IDisposable
{
    private const string Token = "ghs_testtoken0000000000000000000000000000";
    private const string MetadataToken = "ghs_metadataonly000000000000000000000000";
    private readonly string _keyPath = Path.Join(Path.GetTempPath(), $"orchestrator-test-{Guid.NewGuid():N}.pem");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHttpHandler _handler = new();
    private readonly ListLogger<GitHubAppCredentials> _logger = new();
    private MutableOptionsMonitor<RepositoryListOptions>? _repositories;
    private string _installed = """{"repositories":[{"full_name":"thiagoluga/runner-pool-sandbox","private":true,"owner":{"login":"thiagoluga"}},{"full_name":"thiagoluga/other","private":true,"owner":{"login":"thiagoluga"}}]}""";

    [Fact]
    public async Task GetInstallationToken_RepositoryListReloaded_RescopesBeforeExpiry()
    {
        _repositories = TestOptions.Monitor(TestOptions.Repositories("thiagoluga/runner-pool-sandbox"));
        using var credentials = Create();
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        _repositories.CurrentValue = TestOptions.Repositories("thiagoluga/runner-pool-sandbox", "thiagoluga/other");
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        var scoped = ScopedTokenRequests().ToList();
        scoped.Should().HaveCount(2);
        using var body = JsonDocument.Parse(scoped[1].Body!);
        body.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()).Should().Equal("runner-pool-sandbox", "other");
    }

    public GitHubAppCredentialsTests()
    {
        using var key = RSA.Create(2048);
        File.WriteAllText(_keyPath, key.ExportRSAPrivateKeyPem());
        _handler
            .On(HttpMethod.Get, "/users/thiagoluga/installation", HttpStatusCode.OK, """{"id":42,"app_id":12345,"account":{"login":"thiagoluga"}}""")
            .On(HttpMethod.Post, "/app/installations/42/access_tokens", request => FakeHttpHandler.Json(
                HttpStatusCode.Created,
                $$"""{"token":"{{(IsScoped(request) ? Token : MetadataToken)}}","expires_at":"{{_time.GetUtcNow().AddHours(1):O}}"}"""))
            .On(HttpMethod.Get, "/installation/repositories", _ => FakeHttpHandler.Json(HttpStatusCode.OK, _installed))
            .On(HttpMethod.Delete, "/installation/token", HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetInstallationToken_FirstCall_ScopesTokenToInstalledRepositoriesOfOwnerAndPoolPermissions()
    {
        using var credentials = Create();

        var token = await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        token.Should().Be(Token);
        _handler.Requests.Single(r => r.Uri.AbsolutePath == "/installation/repositories").Authorization.Should().Be($"Bearer {MetadataToken}");
        var tokenRequest = ScopedTokenRequests().Single();
        tokenRequest.Authorization.Should().StartWith("Bearer ey");
        using var body = JsonDocument.Parse(tokenRequest.Body!);
        body.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()).Should().Equal("runner-pool-sandbox", "other");
        var permissions = body.RootElement.GetProperty("permissions");
        permissions.GetProperty("actions").GetString().Should().Be("read");
        permissions.GetProperty("administration").GetString().Should().Be("write");
        permissions.GetProperty("metadata").GetString().Should().Be("read");
    }

    [Fact]
    public async Task GetInstallationToken_StillValid_ReusesTokenWithoutCallingGitHub()
    {
        using var credentials = Create();
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(50));
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        ScopedTokenRequests().Should().ContainSingle();
    }

    [Fact]
    public async Task GetInstallationToken_RepositoryRemovedFromList_StaysInScopeForCleanup()
    {
        using var credentials = Create("thiagoluga/not-installed");

        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(ScopedTokenRequests().Single().Body!);
        body.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()).Should().Equal("runner-pool-sandbox", "other");
    }

    [Fact]
    public async Task GetInstallationToken_ConfiguredRepositoryInstalledLater_RescopesAfterInterval()
    {
        using var credentials = Create("thiagoluga/runner-pool-sandbox", "thiagoluga/late");
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        _time.Advance(GitHubAppCredentials.UncoveredRescopeInterval - TimeSpan.FromSeconds(1));
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);
        ScopedTokenRequests().Should().ContainSingle();

        _installed = """{"repositories":[{"full_name":"thiagoluga/runner-pool-sandbox","private":true,"owner":{"login":"thiagoluga"}},{"full_name":"thiagoluga/late","private":true,"owner":{"login":"thiagoluga"}}]}""";
        _time.Advance(TimeSpan.FromSeconds(1));
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        var scoped = ScopedTokenRequests().ToList();
        scoped.Should().HaveCount(2);
        using var body = JsonDocument.Parse(scoped[1].Body!);
        body.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()).Should().Equal("runner-pool-sandbox", "late");
    }

    [Fact]
    public async Task GetInstallationToken_StillUncoveredAfterRescope_WaitsAnotherInterval()
    {
        using var credentials = Create("thiagoluga/runner-pool-sandbox", "thiagoluga/never-installed");
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);
        _time.Advance(GitHubAppCredentials.UncoveredRescopeInterval);
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        _time.Advance(GitHubAppCredentials.UncoveredRescopeInterval - TimeSpan.FromSeconds(1));
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        ScopedTokenRequests().Should().HaveCount(2);
        _logger.AllText.Should().Contain("a listed repository is outside the token");
    }

    [Fact]
    public async Task GetInstallationToken_Rescoped_RevokesReplacedAndMetadataTokens()
    {
        using var credentials = Create("thiagoluga/runner-pool-sandbox", "thiagoluga/never-installed");
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);
        _time.Advance(GitHubAppCredentials.UncoveredRescopeInterval);

        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        _handler.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => r.Authorization)
            .Should().Equal($"Bearer {MetadataToken}", $"Bearer {MetadataToken}", $"Bearer {Token}");
    }

    [Fact]
    public async Task GetInstallationToken_AllConfiguredCovered_DoesNotRescopeEarly()
    {
        using var credentials = Create("thiagoluga/runner-pool-sandbox", "someone/else-owned");
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(30));
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        ScopedTokenRequests().Should().ContainSingle();
    }

    [Fact]
    public async Task GetInstallationToken_InstallationHasNoRepositoryOfOwner_FailsWithoutScopedRequest()
    {
        _installed = """{"repositories":[{"full_name":"someone/else","private":true,"owner":{"login":"someone"}}]}""";
        using var credentials = Create();

        var act = async () => await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<GitHubApiException>()).WithMessage("*inconsistent installed repository identity*");
        ScopedTokenRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task GetInstallationToken_MetadataTokenRequest_AsksOnlyForMetadata()
    {
        using var credentials = Create();

        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        var metadataRequest = _handler.Requests.Single(r => r.Method == HttpMethod.Post && !r.Body!.Contains("repositories", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(metadataRequest.Body!);
        body.RootElement.GetProperty("permissions").EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}").Should().Equal("metadata=read");
    }

    [Fact]
    public async Task GetInstallationToken_CloseToExpiry_RevalidatesTheInstallation()
    {
        using var credentials = Create();
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(56));
        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        ScopedTokenRequests().Should().HaveCount(2);
        _handler.Requests.Count(r => r.Uri.AbsolutePath == "/users/thiagoluga/installation").Should().Be(2);
    }

    [Fact]
    public async Task GetInstallationToken_Renewed_NeverLogsTokenOrJwt()
    {
        using var credentials = Create();

        await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        _logger.AllText.Should().Contain("installation 42")
            .And.NotContain(Token)
            .And.NotContain("ghs_")
            .And.NotContain("Bearer")
            .And.NotContain("-----BEGIN");
    }

    [Fact]
    public async Task GetInstallationToken_MissingKeyFile_FailsWithPathOnly()
    {
        File.Delete(_keyPath);
        using var credentials = Create();

        var act = async () => await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{_keyPath}*");
    }

    [Fact]
    public async Task GetInstallationToken_FileIsNotAKey_FailsWithoutEchoingContent()
    {
        await File.WriteAllTextAsync(_keyPath, "not-a-key-secret-content", TestContext.Current.CancellationToken);
        using var credentials = Create();

        var act = async () => await credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().NotContain("secret-content");
        thrown.Which.InnerException.Should().BeNull();
    }

    [Theory]
    [InlineData("{\"repositories\":[{\"full_name\":\"thiagoluga/shared\",\"private\":true,\"owner\":{\"login\":\"maozinha\"}}]}")]
    [InlineData("{\"repositories\":[{\"full_name\":\"invalid\",\"private\":true,\"owner\":{\"login\":\"thiagoluga\"}}]}")]
    public async Task GetInstallationToken_InconsistentInstalledRepository_RefusesBeforeIssuingPoolToken(string installed)
    {
        _installed = installed;
        using var credentials = Create();
        var act = () => credentials.GetInstallationTokenAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<GitHubApiException>();
        ScopedTokenRequests().Should().BeEmpty();
        _handler.Requests.Where(r => r.Method == HttpMethod.Delete).Should().ContainSingle();
    }

    public void Dispose()
    {
        File.Delete(_keyPath);
        _handler.Dispose();
    }

    private static bool IsScoped(HttpRequestMessage request) =>
        request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("repositories", StringComparison.Ordinal);

    private IEnumerable<RecordedRequest> ScopedTokenRequests() =>
        _handler.Requests.Where(r => r.Method == HttpMethod.Post && r.Body!.Contains("repositories", StringComparison.Ordinal));

    private GitHubAppCredentials Create(params string[] repositories) => new(
        GitHubHttp.CreateClient(new Uri("https://api.github.test/"), _handler),
        TestOptions.Wrap(TestOptions.Orchestrator(_keyPath)),
        _repositories ?? TestOptions.Monitor(TestOptions.Repositories(repositories.Length == 0 ? ["thiagoluga/runner-pool-sandbox"] : repositories)),
        _time,
        _logger);
}
