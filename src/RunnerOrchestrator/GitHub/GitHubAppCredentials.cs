using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.GitHub;

/// <summary>
/// App JWT → installation of the owner → installation token scoped down to the enabled
/// repositories and to the permissions the pool uses (ADR 0002). The token stays in memory and
/// is renewed before it expires.
/// </summary>
internal sealed partial class GitHubAppCredentials(
    HttpClient http,
    IOptions<OrchestratorOptions> options,
    IOptionsMonitor<RepositoryListOptions> repositories,
    TimeProvider time,
    ILogger<GitHubAppCredentials> logger,
    AccountOptions? account = null) : IGitHubCredentialSource, IDisposable
{
    /// <summary>Renew this long before GitHub's one-hour expiry.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often to scope a new token while a configured repository is outside the current one,
    /// so an App installed on it later is picked up without waiting for the hourly renewal.
    /// </summary>
    public static readonly TimeSpan UncoveredRescopeInterval = TimeSpan.FromMinutes(5);

    private readonly AccountOptions _account = account ?? new AccountOptions { Owner = options.Value.Owner };

    public long? InstallationId => _installationId;

    public IEnumerable<RepositoryName> CoveredRepositories => _covered.Select(name => new RepositoryName(_account.Owner, name));

    public bool Covers(RepositoryName repository) =>
        string.Equals(repository.Owner, _account.Owner, StringComparison.OrdinalIgnoreCase) && _covered.Contains(repository.Name);

    public void Invalidate()
    {
        _invalidated = true;
        _covered.Clear();
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _invalidated;
    private RSA? _key;
    private long? _installationId;
    private string? _token;
    private DateTimeOffset _expiresAt;
    private string? _scopedFor;
    private HashSet<string> _covered = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _scopedAt;

    public async Task<string> GetInstallationTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A changed repository list (reloaded from its file) needs a token scoped to it.
            var configured = ConfiguredSignature();
            var now = time.GetUtcNow();
            var reason = RenewalReason(configured, now);
            if (reason is null)
            {
                return _token!;
            }

            var jwt = GitHubAppJwt.Create(options.Value.GitHubApp.AppId, LoadKey(), now);
            _installationId = await FindInstallationAsync(jwt, cancellationToken).ConfigureAwait(false);
            var (response, covered) = await CreateTokenAsync(jwt, _installationId.Value, cancellationToken).ConfigureAwait(false);
            var previous = _token;
            _token = response.Token;
            _expiresAt = response.ExpiresAt;
            _scopedFor = configured;
            _covered = new HashSet<string>(covered, StringComparer.OrdinalIgnoreCase);
            _scopedAt = now;
            _invalidated = false;
            LogTokenRenewed(logger, _installationId.Value, _expiresAt, reason);
            if (previous is not null)
            {
                // Calls of the pool run one after another, so nothing still holds the old token.
                await RevokeAsync(previous, cancellationToken).ConfigureAwait(false);
            }

            return _token!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _key?.Dispose();
        _gate.Dispose();
    }

    private RSA LoadKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        var path = options.Value.GitHubApp.PrivateKeyPath;
        string pem;
        try
        {
            pem = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The App private key is not readable at {path}.", exception);
        }

        var key = RSA.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch (ArgumentException exception)
        {
            key.Dispose();
            // The exception message of ImportFromPem never contains the key, but do not chain it either.
            throw new InvalidOperationException($"The file at {path} is not a PEM private key ({exception.GetType().Name}).");
        }

        _key = key;
        return key;
    }

    private async Task<long> FindInstallationAsync(string jwt, CancellationToken cancellationToken)
    {
        var owner = _account.Owner;
        using var request = GitHubHttp.Request(HttpMethod.Get, $"users/{Uri.EscapeDataString(owner)}/installation", jwt);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var installation = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.InstallationDto, cancellationToken).ConfigureAwait(false)
            ?? throw new GitHubApiException("GitHub returned an empty installation.");
        if (installation.Id <= 0 || installation.AppId != options.Value.GitHubApp.AppId
            || !string.Equals(installation.Account?.Login, owner, StringComparison.OrdinalIgnoreCase)
            || (_account.InstallationId > 0 && installation.Id != _account.InstallationId))
        {
            throw new GitHubApiException("GitHub returned an inconsistent App installation identity.");
        }

        return installation.Id;
    }

    private async Task<(AccessTokenResponse Response, string[] Covered)> CreateTokenAsync(string jwt, long installationId, CancellationToken cancellationToken)
    {
        // Scope to the owner's repositories the installation covers, which by ADR 0002 are the
        // enabled ones. Not to the configured list: a repository removed from the list still has
        // runners to clean up, and asking for one the installation does not cover would fail the
        // whole request. RepositoryGuard decides which repositories get new runners.
        var owner = _account.Owner;
        var names = (await ListInstalledRepositoriesAsync(jwt, installationId, cancellationToken).ConfigureAwait(false))
            .Where(name => string.Equals(name.Owner, owner, StringComparison.OrdinalIgnoreCase))
            .Select(name => name.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            throw new GitHubApiException($"The App installation covers no repository of {owner}.");
        }

        var response = await RequestTokenAsync(jwt, installationId, new AccessTokenRequest(names, new AccessTokenPermissions(Actions: "read", Administration: "write", Metadata: "read")), cancellationToken).ConfigureAwait(false);
        return (response, names);
    }

    private async Task<List<RepositoryName>> ListInstalledRepositoriesAsync(string jwt, long installationId, CancellationToken cancellationToken)
    {
        // A metadata-only token is enough to list what the installation covers.
        var metadata = await RequestTokenAsync(jwt, installationId, new AccessTokenRequest(null, new AccessTokenPermissions(null, null, Metadata: "read")), cancellationToken).ConfigureAwait(false);
        var installed = new List<RepositoryName>();
        try
        {
            await ReadInstalledPagesAsync(metadata.Token!, installed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RevokeAsync(metadata.Token!, CancellationToken.None).ConfigureAwait(false);
        }

        return installed;
    }

    private async Task ReadInstalledPagesAsync(string metadataToken, List<RepositoryName> installed, CancellationToken cancellationToken)
    {
        for (var page = 1; page <= GitHubRunnerApi.MaxPages; page++)
        {
            using var request = GitHubHttp.Request(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"installation/repositories?per_page={GitHubRunnerApi.PageSize}&page={page}"), metadataToken);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await GitHubHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var list = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.InstallationRepositoriesResponse, cancellationToken).ConfigureAwait(false);
            var repositoriesOnPage = list?.Repositories ?? [];
            installed.AddRange(repositoriesOnPage.Select(ParseInstalledRepository).OfType<RepositoryName>());
            if (repositoriesOnPage.Count < GitHubRunnerApi.PageSize)
            {
                break;
            }
        }
    }

    private RepositoryName? ParseInstalledRepository(RepositoryDto repository)
    {
        if (RepositoryName.TryParse(repository.FullName) is not { } name
            || !string.Equals(name.Owner, _account.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repository.Owner?.Login, name.Owner, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubApiException("GitHub returned an inconsistent installed repository identity.");
        }

        return repository.Private ? name : null;
    }

    /// <summary>Best effort: a token left valid only expires on its own within the hour.</summary>
    private async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = GitHubHttp.Request(HttpMethod.Delete, "installation/token", token);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogRevokeFailed(logger, (int)response.StatusCode);
            }
        }
        catch (HttpRequestException exception)
        {
            LogRevokeFailed(logger, (int?)exception.StatusCode ?? 0);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout: the new token is already in hand, so do not fail for this.
            LogRevokeFailed(logger, 0);
        }
    }

    /// <summary>A changed list may follow a new installation; renewing picks it up at once.</summary>
    private string ConfiguredSignature() =>
        string.Join('|', repositories.CurrentValue.Repositories.Select(r => RepositoryName.TryParse(r.Name))
            .OfType<RepositoryName>().Where(r => string.Equals(r.Owner, _account.Owner, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ToString().ToLowerInvariant()).Order(StringComparer.Ordinal));

    /// <summary>Why the token must be scoped again now, or null to keep it.</summary>
    private string? RenewalReason(string configured, DateTimeOffset now)
    {
        if (_token is null || _invalidated)
        {
            return "first token";
        }

        if (_expiresAt - now <= RenewBefore)
        {
            return "close to expiry";
        }

        if (_scopedFor != configured)
        {
            return "repository list changed";
        }

        return UncoveredRescopeDue(now) ? "a listed repository is outside the token" : null;
    }

    /// <summary>A configured repository of the owner the token does not cover, and the last scope is old enough.</summary>
    private bool UncoveredRescopeDue(DateTimeOffset now)
    {
        if (now - _scopedAt < UncoveredRescopeInterval)
        {
            return false;
        }

        var owner = _account.Owner;
        return repositories.CurrentValue.Repositories
            .Select(r => RepositoryName.TryParse(r.Name))
            .OfType<RepositoryName>()
            .Any(name => string.Equals(name.Owner, owner, StringComparison.OrdinalIgnoreCase) && !_covered.Contains(name.Name));
    }

    private async Task<AccessTokenResponse> RequestTokenAsync(string jwt, long installationId, AccessTokenRequest body, CancellationToken cancellationToken)
    {
        using var request = GitHubHttp.Request(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt);
        request.Content = JsonContent.Create(body, GitHubJsonContext.Default.AccessTokenRequest);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var token = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.AccessTokenResponse, cancellationToken).ConfigureAwait(false);
        if (token?.Token is not { Length: > 0 } || token.ExpiresAt <= time.GetUtcNow())
        {
            throw new GitHubApiException("GitHub returned an installation token response without a token.");
        }

        return token;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Installation token renewed for installation {InstallationId}, valid until {ExpiresAt:O} ({Reason})")]
    private static partial void LogTokenRenewed(ILogger logger, long installationId, DateTimeOffset expiresAt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revoking a replaced installation token failed (status {Status}); it expires on its own within the hour")]
    private static partial void LogRevokeFailed(ILogger logger, int status);
}
