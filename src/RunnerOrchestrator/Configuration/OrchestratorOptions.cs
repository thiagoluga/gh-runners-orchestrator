namespace RunnerOrchestrator.Configuration;

/// <summary>The <c>Orchestrator</c> configuration section. Host values live in an example file, not here.</summary>
public sealed class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>Legacy single owner; mutually exclusive with Accounts.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Explicit trusted owner/installation pairs; changing these requires a restart.</summary>
    public IList<AccountOptions> Accounts { get; } = [];

    public bool AllowsOwner(string owner) => Accounts.Count == 0
        ? string.Equals(Owner, owner, StringComparison.OrdinalIgnoreCase)
        : Accounts.Any(a => string.Equals(a.Owner, owner, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<AccountOptions> EffectiveAccounts() => Accounts.Count == 0
        ? [new AccountOptions { Owner = Owner }]
        : Accounts;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>A runner that has not picked a job after this long is removed (ADR 0004).</summary>
    public TimeSpan IdleRunnerTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Labels the pool serves; a queued job is served when all its labels are in this set.</summary>
    public IList<string> RunnerLabels { get; } = [];

    /// <summary>Pinned runner image, pulled by the host.</summary>
    public string RunnerImage { get; set; } = string.Empty;

    /// <summary>Docker socket path, the same on the host and inside the orchestrator container.</summary>
    public string DockerSocket { get; set; } = "/var/run/docker.sock";

    /// <summary>Group id of the Docker socket on the host, added to every runner container.</summary>
    public int DockerSocketGroupId { get; set; }

    public GitHubAppOptions GitHubApp { get; set; } = new();

    /// <summary>Optional App registry; mutually exclusive with a configured legacy GitHubApp.</summary>
    public IList<GitHubAppOptions> GitHubApps { get; } = [];

    public IEnumerable<GitHubAppOptions> EffectiveApps() => GitHubApps.Count == 0 ? [GitHubApp] : GitHubApps;

    public GitHubAppOptions AppFor(AccountOptions account) => GitHubApps.Count == 0
        ? GitHubApp
        : GitHubApps.Single(app => app.AppId == account.AppId);

    public GitHubAppOptions AppForOwner(string owner) => AppFor(EffectiveAccounts()
        .Single(account => string.Equals(account.Owner, owner, StringComparison.OrdinalIgnoreCase)));

    public IList<SlotOptions> Slots { get; } = [];

    /// <summary>Port of the status endpoint (<c>/healthz</c>, <c>/metrics</c>); 0 turns it off.</summary>
    public int StatusPort { get; set; }

    /// <summary>
    /// Host part of the status endpoint's listen prefix. <c>+</c> listens on every interface of the
    /// orchestrator container; the deployment decides what reaches it (loopback only by default).
    /// </summary>
    public string StatusHost { get; set; } = "+";

    /// <summary>This host's name in a pool of several hosts (ADR 0013); empty with one host.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Every host of the pool, in the same order on every host; empty with one host.</summary>
    public IList<string> PoolHosts { get; } = [];

    /// <summary>A queued job owned by another host may be served here after waiting this long (ADR 0013).</summary>
    public TimeSpan TakeOverAfter { get; set; } = TimeSpan.FromMinutes(1);
}
