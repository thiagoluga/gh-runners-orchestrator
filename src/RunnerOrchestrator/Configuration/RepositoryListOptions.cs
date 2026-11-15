namespace RunnerOrchestrator.Configuration;

/// <summary>The repository list, kept in its own file so enabling a repository touches nothing else.</summary>
public sealed class RepositoryListOptions
{
    public IList<RepositoryOptions> Repositories { get; } = [];
}
