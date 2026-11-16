namespace RunnerOrchestrator.Configuration;

/// <summary>A trusted repository owner and its installation of the configured GitHub App.</summary>
public sealed class AccountOptions
{
    public string Owner { get; set; } = string.Empty;

    public long InstallationId { get; set; }
}
