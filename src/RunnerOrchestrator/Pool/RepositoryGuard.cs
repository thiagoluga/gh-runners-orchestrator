using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// ADR 0011: only private repositories of explicitly trusted accounts get runners. Checked against the
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
        var entries = configured.ToList();
        var duplicates = entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = new List<EnabledRepository>();
        foreach (var entry in entries)
        {
            var reason = duplicates.Contains(entry.Name) ? "duplicate repository configuration"
                : entry.MaxConcurrent < 0 ? "negative MaxConcurrent"
                : await CheckAsync(entry.Name, cancellationToken).ConfigureAwait(false);
            if (reason is null)
            {
                var parsed = RepositoryName.Parse(entry.Name);
                allowed.Add(new EnabledRepository(parsed, entry.MaxConcurrent));
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

        var owner = name.Owner;
        if (!options.Value.AllowsOwner(name.Owner))
        {
            return "owner is not authorized";
        }

        RepositoryInfo info;
        try
        {
            info = await api.GetRepositoryAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is GitHubApiException or System.Text.Json.JsonException or HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return "not readable by the App installation";
        }

        if (!info.IsPrivate)
        {
            return "public";
        }

        return info.Name == name && string.Equals(info.OwnerLogin, owner, StringComparison.OrdinalIgnoreCase)
            ? null
            : "repository identity is inconsistent according to GitHub";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repository {Repository} refused: {Reason}. It gets no runners until this changes.")]
    private static partial void LogRefused(ILogger logger, string repository, string reason);
}

internal sealed record EnabledRepository(RepositoryName Name, int MaxConcurrent);
