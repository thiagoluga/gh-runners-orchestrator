using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.GitHub;

/// <summary>Independent installation clients behind the one global pool coordinator.</summary>
internal sealed class InstallationRouter : IGitHubRunnerApi, IDisposable
{
    private readonly Dictionary<string, InstallationClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly IOptionsMonitor<RepositoryListOptions> _repositories;

    public InstallationRouter(HttpClient http, IOptions<OrchestratorOptions> options,
        IOptionsMonitor<RepositoryListOptions> repositories, TimeProvider time, ILoggerFactory loggers)
    {
        _http = http;
        _time = time;
        _repositories = repositories;
        foreach (var account in options.Value.EffectiveAccounts())
        {
            var app = options.Value.AppFor(account);
            var credentials = new GitHubAppCredentials(http, options, repositories, time,
                loggers.CreateLogger<GitHubAppCredentials>(), account, app);
            _clients.Add(account.Owner, new InstallationClient(credentials, new GitHubRunnerApi(http, credentials), app.AppId));
        }
    }

    // A global budget would suspend healthy installations when another one is exhausted.
    public RateLimitStatus? RateLimit => null;

    public long? GetAppId(RepositoryName repository) => Find(repository).AppId;

    public long? GetInstallationId(RepositoryName repository) => Find(repository).Credentials.InstallationId;

    public RateLimitStatus? GetRateLimit(RepositoryName repository) => Find(repository).Api.RateLimit;

    public async Task<IReadOnlyList<RepositoryName>> ListCleanupRepositoriesAsync(CancellationToken cancellationToken)
    {
        var repositories = new List<RepositoryName>();
        foreach (var client in _clients.Values)
        {
            var now = _time.GetUtcNow();
            if (client.RetryAfter > now || client.Api.RateLimit is { IsLow: true } rate && rate.ResetsAt > now)
            {
                continue;
            }

            try
            {
                await client.Credentials.GetInstallationTokenAsync(cancellationToken).ConfigureAwait(false);
                repositories.AddRange(client.Credentials.CoveredRepositories);
            }
            catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
            {
                BackOff(client, _time.GetUtcNow());
                // A removed or unavailable installation never redirects cleanup to another account.
            }
        }

        return repositories;
    }

    public Task<IReadOnlyList<long>> ListRunIdsAsync(RepositoryName repository, string status, CancellationToken cancellationToken) =>
        ExecuteAsync(repository, api => api.ListRunIdsAsync(repository, status, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<WorkflowJob>> ListJobsAsync(RepositoryName repository, long runId, CancellationToken cancellationToken) =>
        ExecuteAsync(repository, api => api.ListJobsAsync(repository, runId, cancellationToken), cancellationToken);

    public Task<JitRunner> GenerateJitConfigAsync(RepositoryName repository, string runnerName, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var listed = _repositories.CurrentValue.Repositories.Where(r => RepositoryName.TryParse(r.Name) == repository).ToList();
        if (listed.Count != 1 || listed[0].MaxConcurrent < 0)
        {
            throw new GitHubApiException("Repository is not uniquely enabled in the current allowlist.");
        }

        return ExecuteAsync(repository, async api =>
        {
            var info = await api.GetRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!info.IsPrivate)
            {
                throw new GitHubApiException("Public repositories cannot receive pool runners.");
            }

            return await api.GenerateJitConfigAsync(repository, runnerName, labels, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SelfHostedRunner>> ListRunnersAsync(RepositoryName repository, CancellationToken cancellationToken) =>
        ExecuteAsync(repository, api => api.ListRunnersAsync(repository, cancellationToken), cancellationToken);

    public async Task DeleteRunnerAsync(RepositoryName repository, long runnerId, CancellationToken cancellationToken) =>
        await ExecuteAsync(repository, async api =>
        {
            await api.DeleteRunnerAsync(repository, runnerId, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    public Task<RepositoryInfo> GetRepositoryAsync(RepositoryName repository, CancellationToken cancellationToken) =>
        ExecuteAsync(repository, api => api.GetRepositoryAsync(repository, cancellationToken), cancellationToken);

    public Task<string> GetLatestRunnerVersionAsync(CancellationToken cancellationToken) =>
        new GitHubRunnerApi(_http, _clients.Values.First().Credentials).GetLatestRunnerVersionAsync(cancellationToken);

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Credentials.Dispose();
        }
    }

    private InstallationClient Find(RepositoryName repository) => _clients.TryGetValue(repository.Owner, out var client)
        ? client : throw new GitHubApiException("Repository owner is not authorized.");

    private async Task<T> ExecuteAsync<T>(RepositoryName repository, Func<GitHubRunnerApi, Task<T>> action, CancellationToken cancellationToken)
    {
        var client = Find(repository);
        var now = _time.GetUtcNow();
        if (client.RetryAfter > now)
        {
            throw new GitHubApiException("This installation is temporarily unavailable; retry on a later cycle.");
        }

        if (client.Api.RateLimit is { IsLow: true } rate && rate.ResetsAt > now)
        {
            throw new GitHubApiException("Rate limit low for this installation; waiting for reset.");
        }

        try
        {
            await client.Credentials.GetInstallationTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            BackOff(client, _time.GetUtcNow());
            throw;
        }

        if (!client.Credentials.Covers(repository))
        {
            throw new GitHubApiException("Repository is not private and covered by the owner's installation.");
        }

        try
        {
            return await action(client.Api).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            // Never retry through another installation, and never reuse cached authorization.
            if (exception is GitHubApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests })
            {
                BackOff(client, _time.GetUtcNow());
            }

            throw;
        }
    }

    private static bool IsUnavailable(Exception exception, CancellationToken cancellationToken) =>
        exception is GitHubApiException or GitHubCredentialException or System.Text.Json.JsonException or HttpRequestException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static void BackOff(InstallationClient client, DateTimeOffset now)
    {
        client.Credentials.Invalidate();
        client.Api.ClearCache();
        client.RetryAfter = now + TimeSpan.FromSeconds(20);
    }

    private sealed class InstallationClient(GitHubAppCredentials credentials, GitHubRunnerApi api, long appId)
    {
        public long AppId { get; } = appId;

        public GitHubAppCredentials Credentials { get; } = credentials;

        public GitHubRunnerApi Api { get; } = api;

        public DateTimeOffset RetryAfter { get; set; }
    }
}
