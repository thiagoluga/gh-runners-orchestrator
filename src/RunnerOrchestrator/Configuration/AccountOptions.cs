namespace RunnerOrchestrator.Configuration;

/// <summary>A trusted repository owner and its installation of the configured GitHub App.</summary>
public sealed class AccountOptions
{
    public string Owner { get; set; } = string.Empty;

    /// <summary>Required in registry mode; inferred only for the legacy single App.</summary>
    public long AppId { get; set; }

    public long InstallationId { get; set; }
}
