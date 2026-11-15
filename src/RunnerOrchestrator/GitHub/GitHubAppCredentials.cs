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
    ILogger<GitHubAppCredentials> logger) : IGitHubCredentialSource, IDisposable
{
    /// <summary>Renew this long before GitHub's one-hour expiry.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private RSA? _key;
    private long? _installationId;
    private string? _token;
    private DateTimeOffset _expiresAt;
    private string? _scopedFor;

    public async Task<string> GetInstallationTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A changed repository list (reloaded from its file) needs a token scoped to it.
            var configured = ConfiguredSignature();
            if (_token is not null && _expiresAt - time.GetUtcNow() > RenewBefore && _scopedFor == configured)
            {
                return _token;
            }

            var jwt = GitHubAppJwt.Create(options.Value.GitHubApp.AppId, LoadKey(), time.GetUtcNow());
            _installationId ??= await FindInstallationAsync(jwt, cancellationToken).ConfigureAwait(false);
            var response = await CreateTokenAsync(jwt, _installationId.Value, cancellationToken).ConfigureAwait(false);
            _token = response.Token;
            _expiresAt = response.ExpiresAt;
            _scopedFor = configured;
            LogTokenRenewed(logger, _installationId.Value, _expiresAt);
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
        var owner = options.Value.Owner;
        using var request = GitHubHttp.Request(HttpMethod.Get, $"users/{Uri.EscapeDataString(owner)}/installation", jwt);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var installation = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.InstallationDto, cancellationToken).ConfigureAwait(false)
            ?? throw new GitHubApiException("GitHub returned an empty installation.");
        return installation.Id;
    }

    private async Task<AccessTokenResponse> CreateTokenAsync(string jwt, long installationId, CancellationToken cancellationToken)
    {
        // Scope to the owner's repositories the installation covers, which by ADR 0002 are the
        // enabled ones. Not to the configured list: a repository removed from the list still has
        // runners to clean up, and asking for one the installation does not cover would fail the
        // whole request. RepositoryGuard decides which repositories get new runners.
        var owner = options.Value.Owner;
        var names = (await ListInstalledRepositoriesAsync(jwt, installationId, cancellationToken).ConfigureAwait(false))
            .Where(name => string.Equals(name.Owner, owner, StringComparison.OrdinalIgnoreCase))
            .Select(name => name.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            throw new GitHubApiException($"The App installation covers no repository of {owner}.");
        }

        return await RequestTokenAsync(jwt, installationId, new AccessTokenRequest(names, new AccessTokenPermissions(Actions: "read", Administration: "write", Metadata: "read")), cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<RepositoryName>> ListInstalledRepositoriesAsync(string jwt, long installationId, CancellationToken cancellationToken)
    {
        // A metadata-only token is enough to list what the installation covers.
        var metadata = await RequestTokenAsync(jwt, installationId, new AccessTokenRequest(null, new AccessTokenPermissions(null, null, Metadata: "read")), cancellationToken).ConfigureAwait(false);
        var installed = new List<RepositoryName>();
        for (var page = 1; page <= GitHubRunnerApi.MaxPages; page++)
        {
            using var request = GitHubHttp.Request(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"installation/repositories?per_page={GitHubRunnerApi.PageSize}&page={page}"), metadata.Token!);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await GitHubHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var list = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.InstallationRepositoriesResponse, cancellationToken).ConfigureAwait(false);
            var repositoriesOnPage = list?.Repositories ?? [];
            installed.AddRange(repositoriesOnPage.Select(r => RepositoryName.TryParse(r.FullName)).OfType<RepositoryName>());
            if (repositoriesOnPage.Count < GitHubRunnerApi.PageSize)
            {
                break;
            }
        }

        return installed;
    }

    /// <summary>A changed list may follow a new installation; renewing picks it up at once.</summary>
    private string ConfiguredSignature() =>
        string.Join('|', repositories.CurrentValue.Repositories.Select(r => r.Name.ToLowerInvariant()).Order(StringComparer.Ordinal));
    private async Task<AccessTokenResponse> RequestTokenAsync(string jwt, long installationId, AccessTokenRequest body, CancellationToken cancellationToken)
    {
        using var request = GitHubHttp.Request(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt);
        request.Content = JsonContent.Create(body, GitHubJsonContext.Default.AccessTokenRequest);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var token = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.AccessTokenResponse, cancellationToken).ConfigureAwait(false);
        if (token?.Token is not { Length: > 0 })
        {
            throw new GitHubApiException("GitHub returned an installation token response without a token.");
        }

        return token;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Installation token renewed for installation {InstallationId}, valid until {ExpiresAt:O}")]
    private static partial void LogTokenRenewed(ILogger logger, long installationId, DateTimeOffset expiresAt);
}
