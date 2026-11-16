namespace RunnerOrchestrator.GitHub;

/// <summary>A local credential failure with a safe message and no cryptographic details.</summary>
internal sealed class GitHubCredentialException(string message) : InvalidOperationException(message);
