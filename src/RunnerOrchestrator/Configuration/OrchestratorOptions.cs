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

    public IList<SlotOptions> Slots { get; } = [];
}
