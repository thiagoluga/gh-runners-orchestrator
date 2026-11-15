namespace RunnerOrchestrator.Configuration;

/// <summary>One runner at a time, on its own cores (cpuset, never a CPU quota) and memory limit.</summary>
public sealed class SlotOptions
{
    public string Name { get; set; } = string.Empty;

    public string Cpuset { get; set; } = string.Empty;

    /// <summary>Memory limit such as <c>16g</c>; swap is set to the same value.</summary>
    public string Memory { get; set; } = string.Empty;
}
