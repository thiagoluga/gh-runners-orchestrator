using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.GitHub;

/// <summary>
/// GitHub REST client for the pool. GET requests are conditional (<c>If-None-Match</c>): a
/// <c>304</c> does not count against the rate limit, which keeps an idle pool almost free.
/// </summary>
internal sealed class GitHubRunnerApi(HttpClient http, IGitHubCredentialSource credentials) : IGitHubRunnerApi
{
    /// <summary>Stale ETag entries are dropped past this size; the next GET simply is not conditional.</summary>
    internal const int MaxCachedResponses = 500;

    /// <summary>GitHub's largest page.</summary>
    internal const int PageSize = 100;

    /// <summary>Bounds one listing to 1,000 items; demand beyond that already exceeds any pool.</summary>
    internal const int MaxPages = 10;

    private readonly ConcurrentDictionary<string, CachedResponse> _cache = new(StringComparer.Ordinal);

    public void ClearCache() => _cache.Clear();

    public long? GetInstallationId(RepositoryName repository) => credentials.InstallationId;

    public RateLimitStatus? GetRateLimit(RepositoryName repository) => RateLimit;

    public RateLimitStatus? RateLimit { get; private set; }

    public async Task<IReadOnlyList<long>> ListRunIdsAsync(RepositoryName repository, string status, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var response = await GetAsync(PagePath($"repos/{repository}/actions/runs?status={Uri.EscapeDataString(status)}&per_page={PageSize}", page), GitHubJsonContext.Default.WorkflowRunsResponse, cancellationToken).ConfigureAwait(false);
            ids.AddRange(response.WorkflowRuns.Select(r => r.Id));
            if (response.WorkflowRuns.Count < PageSize)
            {
                break;
            }
        }

        return ids;
    }

    public async Task<IReadOnlyList<WorkflowJob>> ListJobsAsync(RepositoryName repository, long runId, CancellationToken cancellationToken)
    {
        var jobs = new List<WorkflowJob>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var response = await GetAsync(PagePath($"repos/{repository}/actions/runs/{runId}/jobs?filter=latest&per_page={PageSize}", page), GitHubJsonContext.Default.JobsResponse, cancellationToken).ConfigureAwait(false);
            jobs.AddRange(response.Jobs.Select(j => new WorkflowJob(j.Id, j.RunId, j.Status ?? string.Empty, j.Labels ?? [], j.CreatedAt)));
            if (response.Jobs.Count < PageSize)
            {
                break;
            }
        }

        return jobs;
    }

    /// <summary>The first page keeps the plain URL so its ETag stays the same across cycles.</summary>
    private static string PagePath(string path, int page) =>
        page == 1 ? path : string.Create(CultureInfo.InvariantCulture, $"{path}&page={page}");

    public async Task<JitRunner> GenerateJitConfigAsync(RepositoryName repository, string runnerName, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var body = new JitConfigRequest(runnerName, RunnerGroupId: 1, labels, WorkFolder: "_work");
        using var request = await CreateRequestAsync(HttpMethod.Post, $"repos/{repository}/actions/runners/generate-jitconfig", cancellationToken).ConfigureAwait(false);
        request.Content = JsonContent.Create(body, GitHubJsonContext.Default.JitConfigRequest);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.JitConfigResponse, cancellationToken).ConfigureAwait(false);
        if (result?.Runner is null || string.IsNullOrEmpty(result.EncodedJitConfig)
            || result.Runner.Id <= 0 || !string.Equals(result.Runner.Name, runnerName, StringComparison.Ordinal))
        {
            throw new GitHubApiException("GitHub returned a JIT configuration response without a runner or configuration.");
        }

        return new JitRunner(result.Runner.Id, result.Runner.Name, result.EncodedJitConfig);
    }

    public async Task<IReadOnlyList<SelfHostedRunner>> ListRunnersAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        var response = await GetAsync($"repos/{repository}/actions/runners?per_page=100", GitHubJsonContext.Default.RunnersResponse, cancellationToken).ConfigureAwait(false);
        return [.. response.Runners.Select(r => new SelfHostedRunner(r.Id, r.Name, r.Status ?? string.Empty, r.Busy))];
    }

    public async Task DeleteRunnerAsync(RepositoryName repository, long runnerId, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, $"repos/{repository}/actions/runners/{runnerId}", cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(request, cancellationToken, allowNotFound: true).ConfigureAwait(false);
    }

    public async Task<RepositoryInfo> GetRepositoryAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        var response = await GetAsync($"repos/{repository}", GitHubJsonContext.Default.RepositoryDto, cancellationToken).ConfigureAwait(false);
        if (RepositoryName.TryParse(response.FullName) is not { } name || name != repository
            || !string.Equals(response.Owner?.Login, repository.Owner, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubApiException("GitHub returned an inconsistent repository identity.");
        }

        return new RepositoryInfo(name, response.Private, response.Owner!.Login);
    }

    public async Task<string> GetLatestRunnerVersionAsync(CancellationToken cancellationToken)
    {
        // A public repository outside the installation: read it without the installation token.
        using var request = new HttpRequestMessage(HttpMethod.Get, "repos/actions/runner/releases/latest");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var release = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.ReleaseDto, cancellationToken).ConfigureAwait(false)
            ?? throw new GitHubApiException("GitHub returned an empty release.");
        return release.TagName.TrimStart('v');
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        if (_cache.TryGetValue(path, out var cached))
        {
            request.Headers.IfNoneMatch.Add(cached.ETag);
        }

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] body;
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            body = cached.Body;
        }
        else
        {
            body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Remember(path, response.Headers.ETag, body);
        }

        return JsonSerializer.Deserialize(body, typeInfo)
            ?? throw new GitHubApiException($"GitHub returned an empty body for GET {path}.");
    }

    private void Remember(string path, EntityTagHeaderValue? etag, byte[] body)
    {
        if (etag is null)
        {
            _cache.TryRemove(path, out _);
            return;
        }

        if (_cache.Count >= MaxCachedResponses)
        {
            _cache.Clear();
        }

        _cache[path] = new CachedResponse(etag, body);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var token = await credentials.GetInstallationTokenAsync(cancellationToken).ConfigureAwait(false);
        return GitHubHttp.Request(method, path, token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            ReadRateLimit(response);
            if (!(allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
            {
                await GitHubHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private void ReadRateLimit(HttpResponseMessage response)
    {
        if (TryReadLong(response, "x-ratelimit-limit", out var limit)
            && TryReadLong(response, "x-ratelimit-remaining", out var remaining)
            && TryReadLong(response, "x-ratelimit-reset", out var reset))
        {
            RateLimit = new RateLimitStatus((int)limit, (int)remaining, DateTimeOffset.FromUnixTimeSeconds(reset));
        }
    }

    private static bool TryReadLong(HttpResponseMessage response, string header, out long value)
    {
        value = 0;
        return response.Headers.TryGetValues(header, out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private sealed record CachedResponse(EntityTagHeaderValue ETag, byte[] Body);
}
