namespace RunnerOrchestrator.GitHub;

/// <summary>Hands out a valid installation token; callers never cache or log it.</summary>
internal interface IGitHubCredentialSource
{
    long? InstallationId => null;

    Task<string> GetInstallationTokenAsync(CancellationToken cancellationToken);
}
