using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// ADR 0006: only private repositories of the configured owner get runners. Checked against the
/// API on every cycle (a conditional request, so almost always free), never trusted from the
/// configuration alone.
/// </summary>
internal sealed partial class RepositoryGuard(
    IGitHubRunnerApi api,
    IOptions<OrchestratorOptions> options,
    ILogger<RepositoryGuard> logger)
{
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<EnabledRepository>> FilterAsync(IEnumerable<RepositoryOptions> configured, CancellationToken cancellationToken)
    {
        var allowed = new List<EnabledRepository>();
        foreach (var entry in configured)
        {
            var reason = await CheckAsync(entry.Name, cancellationToken).ConfigureAwait(false);
            if (reason is null)
            {
                allowed.Add(new EnabledRepository(RepositoryName.Parse(entry.Name), entry.MaxConcurrent));
                _reported.RemoveWhere(key => key.StartsWith(entry.Name + "|", StringComparison.OrdinalIgnoreCase));
            }
            else if (_reported.Add($"{entry.Name}|{reason}"))
            {
                LogRefused(logger, entry.Name, reason);
            }
        }

        return allowed;
    }

    private async Task<string?> CheckAsync(string configuredName, CancellationToken cancellationToken)
    {
        if (RepositoryName.TryParse(configuredName) is not { } name)
        {
            return "not an owner/name repository";
        }

        var owner = options.Value.Owner;
        if (!string.Equals(name.Owner, owner, StringComparison.OrdinalIgnoreCase))
        {
            return $"not owned by {owner}";
        }

        RepositoryInfo info;
        try
        {
            info = await api.GetRepositoryAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException exception)
        {
            return $"not readable by the App installation ({exception.StatusCode?.ToString() ?? "no status"})";
        }

        if (!info.IsPrivate)
        {
            return "public";
        }

        return string.Equals(info.OwnerLogin, owner, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"owned by {info.OwnerLogin} according to GitHub";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repository {Repository} refused: {Reason}. It gets no runners until this changes.")]
    private static partial void LogRefused(ILogger logger, string repository, string reason);
}

internal sealed record EnabledRepository(RepositoryName Name, int MaxConcurrent);
