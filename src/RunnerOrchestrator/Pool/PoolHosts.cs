using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// Which host owns a runner and which host serves a queued job when several orchestrators
/// share one pool label (ADR 0013). With no <c>PoolHosts</c> every <c>orch-</c> runner is this
/// host's and every job may be served here, as with one host.
/// </summary>
internal sealed class PoolHosts
{
    public const string RunnerPrefix = "orch-";

    /// <summary>Separates the host from the rest of a runner name; account names never contain it.</summary>
    public const string HostSeparator = "--";

    private const int MaxHostLength = 16;

    private readonly IReadOnlyList<string> _hosts;
    private readonly IReadOnlyList<string> _longestFirst;
    private readonly TimeSpan _takeOverAfter;

    public PoolHosts(IOptions<OrchestratorOptions> options)
        : this(options.Value.Host, [.. options.Value.PoolHosts], options.Value.TakeOverAfter)
    {
    }

    public PoolHosts(string self, IReadOnlyList<string> hosts, TimeSpan takeOverAfter)
    {
        Self = self;
        _hosts = hosts;
        _longestFirst = [.. hosts.OrderByDescending(h => h.Length)];
        _takeOverAfter = takeOverAfter;
    }

    /// <summary>This host's name; empty with one host.</summary>
    public string Self { get; }

    public bool IsMultiHost => _hosts.Count > 0;

    /// <summary>Lower-case letters and digits with single inner hyphens, 1 to 16 characters.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxHostLength
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && name[0] != '-' && name[^1] != '-'
        && !name.Contains(HostSeparator, StringComparison.Ordinal);

    /// <summary>The start of every runner name this host creates.</summary>
    public string NamePrefix => NamePrefixFor(Self, IsMultiHost);

    public static string NamePrefixFor(OrchestratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return NamePrefixFor(options.Host, options.PoolHosts.Count > 0);
    }

    private static string NamePrefixFor(string host, bool multiHost) => multiHost ? RunnerPrefix + host + HostSeparator : RunnerPrefix;

    /// <summary>
    /// The host that owns a runner, or null for a name that is not an orchestrator runner or
    /// belongs to a host outside the pool. A single-host name belongs to the first host.
    /// </summary>
    public string? OwnerOf(string runnerName)
    {
        if (!runnerName.StartsWith(RunnerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!IsMultiHost)
        {
            return Self;
        }

        var rest = runnerName[RunnerPrefix.Length..];
        foreach (var host in _longestFirst)
        {
            if (rest.StartsWith(host + HostSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return host;
            }
        }

        var separator = rest.IndexOf(HostSeparator, StringComparison.Ordinal);
        return separator > 0 && IsValidName(rest[..separator].ToLowerInvariant()) ? null : _hosts[0];
    }

    public bool IsOwn(string runnerName) => OwnerOf(runnerName) is { } owner && string.Equals(owner, Self, StringComparison.Ordinal);

    /// <summary>An orchestrator runner of another host of the pool.</summary>
    public bool IsForeign(string runnerName) => OwnerOf(runnerName) is { } owner && !string.Equals(owner, Self, StringComparison.Ordinal);

    public string OwnerOfJob(long jobId) => IsMultiHost ? _hosts[(int)((ulong)jobId % (ulong)_hosts.Count)] : Self;

    /// <summary>
    /// A job this host owns, or one another host left waiting longer than the takeover delay.
    /// The wait counts from when this host first saw the job queued: a job that waited for a
    /// <c>needs</c> keeps its run's creation time, so GitHub's times would make it look old.
    /// </summary>
    public bool MayServe(WorkflowJob job, DateTimeOffset firstSeenQueued, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        return !IsMultiHost
            || string.Equals(OwnerOfJob(job.Id), Self, StringComparison.Ordinal)
            || now - firstSeenQueued >= _takeOverAfter;
    }
}
