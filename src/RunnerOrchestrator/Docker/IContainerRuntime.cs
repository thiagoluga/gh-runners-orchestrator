namespace RunnerOrchestrator.Docker;

/// <summary>The Docker Engine operations the pool uses (ADR 0003, 0004). Faked in unit tests.</summary>
internal interface IContainerRuntime
{
    /// <summary>Pulls the image when the host does not have it yet.</summary>
    Task EnsureImageAsync(string image, CancellationToken cancellationToken);

    /// <summary>Creates a container and returns its id.</summary>
    Task<string> CreateAsync(ContainerSpec spec, CancellationToken cancellationToken);

    Task StartAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>Containers carrying the given label (<c>key=value</c>), in any state.</summary>
    Task<IReadOnlyList<ContainerSummary>> ListByLabelAsync(string label, CancellationToken cancellationToken);

    Task<ContainerState> InspectAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>Removes the container and its anonymous volumes; a missing container is not an error.</summary>
    Task RemoveAsync(string containerId, CancellationToken cancellationToken);
}

internal sealed record ContainerSummary(string Id, string Name, string State, IReadOnlyDictionary<string, string> Labels);

internal sealed record ContainerState(string Status, int ExitCode, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <summary>Everything that makes a runner container; the environment may hold a secret and is never logged.</summary>
internal sealed record ContainerSpec(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Environment,
    string Cpuset,
    long MemoryBytes,
    int NofileLimit,
    string DockerSocket,
    int DockerSocketGroupId)
{
    public override string ToString() => $"ContainerSpec {Name} ({Image}, cpuset {Cpuset})";
}
