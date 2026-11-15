namespace RunnerOrchestrator.Pool;

/// <summary>The labels a pool runner carries: GitHub's defaults for a Linux x64 runner plus the configured ones.</summary>
internal static class ServedLabels
{
    public static readonly IReadOnlyList<string> Defaults = ["self-hosted", "linux", "x64"];

    public static IReadOnlyList<string> For(IEnumerable<string> configured) =>
        [.. Defaults.Concat(configured).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A job is served when every label it asks for is one the pool's runners carry.</summary>
    public static bool Serves(IReadOnlyList<string> served, IReadOnlyList<string> jobLabels) =>
        jobLabels.Count > 0 && jobLabels.All(label => served.Contains(label, StringComparer.OrdinalIgnoreCase));
}
