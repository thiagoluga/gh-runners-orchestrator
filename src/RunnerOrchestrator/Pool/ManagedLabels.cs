namespace RunnerOrchestrator.Pool;

/// <summary>Docker labels that hold the pool's state (ADR 0004). Never a secret here.</summary>
internal static class ManagedLabels
{
    public const string Managed = "orchestrator.managed";
    public const string Repository = "orchestrator.repo";
    public const string Slot = "orchestrator.slot";
    public const string RunnerName = "orchestrator.runner-name";
    public const string RunnerId = "orchestrator.runner-id";
    public const string CreatedAt = "orchestrator.created-at";

    public const string ManagedFilter = Managed + "=true";
}
