namespace RunnerOrchestrator.Configuration;

/// <summary>The App id is not secret; the key is a file whose content never enters configuration.</summary>
public sealed class GitHubAppOptions
{
    public long AppId { get; set; }

    public string PrivateKeyPath { get; set; } = "/run/secrets/github-app.pem";

    public Uri ApiBaseUrl { get; set; } = new("https://api.github.com/");
}
