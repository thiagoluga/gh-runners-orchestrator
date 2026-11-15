namespace RunnerOrchestrator.Configuration;

public sealed class RepositoryOptions
{
    /// <summary><c>owner/name</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Most runners this repository may hold at once; 0 means no cap beyond the slots.</summary>
    public int MaxConcurrent { get; set; }
}
