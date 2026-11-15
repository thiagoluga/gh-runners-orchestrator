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
    IOptions<RepositoryListOptions> repositories,
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

    public async Task<string> GetInstallationTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && _expiresAt - time.GetUtcNow() > RenewBefore)
            {
                return _token;
            }

            var jwt = GitHubAppJwt.Create(options.Value.GitHubApp.AppId, LoadKey(), time.GetUtcNow());
            _installationId ??= await FindInstallationAsync(jwt, cancellationToken).ConfigureAwait(false);
            var response = await CreateTokenAsync(jwt, _installationId.Value, cancellationToken).ConfigureAwait(false);
            _token = response.Token;
            _expiresAt = response.ExpiresAt;
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
        // Scope only to configured repositories the installation covers: asking for one it does
        // not cover fails the whole request, which would stop the pool for every repository.
        // RepositoryGuard then refuses, and logs, the uncovered ones.
        var installed = await ListInstalledRepositoriesAsync(jwt, installationId, cancellationToken).ConfigureAwait(false);
        var owner = options.Value.Owner;
        var names = repositories.Value.Repositories
            .Select(r => TryParse(r.Name))
            .OfType<RepositoryName>()
            .Where(name => string.Equals(name.Owner, owner, StringComparison.OrdinalIgnoreCase) && installed.Contains(name))
            .Select(name => name.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            throw new GitHubApiException("None of the configured repositories is covered by the App installation.");
        }

        return await RequestTokenAsync(jwt, installationId, new AccessTokenRequest(names, new AccessTokenPermissions(Actions: "read", Administration: "write", Metadata: "read")), cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<RepositoryName>> ListInstalledRepositoriesAsync(string jwt, long installationId, CancellationToken cancellationToken)
    {
        // A metadata-only token is enough to list what the installation covers.
        var metadata = await RequestTokenAsync(jwt, installationId, new AccessTokenRequest(null, new AccessTokenPermissions(null, null, Metadata: "read")), cancellationToken).ConfigureAwait(false);
        using var request = GitHubHttp.Request(HttpMethod.Get, "installation/repositories?per_page=100", metadata.Token!);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var list = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.InstallationRepositoriesResponse, cancellationToken).ConfigureAwait(false);
        return [.. (list?.Repositories ?? []).Select(r => TryParse(r.FullName)).OfType<RepositoryName>()];
    }

    private static RepositoryName? TryParse(string value)
    {
        try
        {
            return RepositoryName.Parse(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

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
