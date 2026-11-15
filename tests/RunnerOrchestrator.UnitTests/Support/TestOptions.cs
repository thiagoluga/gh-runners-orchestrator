using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;

namespace RunnerOrchestrator.UnitTests.Support;

internal static class TestOptions
{
    public static OrchestratorOptions Orchestrator(string keyPath = "/nonexistent/key.pem")
    {
        var options = new OrchestratorOptions
        {
            Owner = "thiagoluga",
            RunnerImage = "ghcr.io/thiagoluga/gh-runners-orchestrator-runner:test",
            DockerSocketGroupId = 988,
            GitHubApp = new GitHubAppOptions { AppId = 12345, PrivateKeyPath = keyPath },
        };
        options.RunnerLabels.Add("vm-pool");
        options.Slots.Add(new SlotOptions { Name = "slot-1", Cpuset = "0-4", Memory = "16g" });
        options.Slots.Add(new SlotOptions { Name = "slot-2", Cpuset = "5-9", Memory = "16g" });
        return options;
    }

    public static RepositoryListOptions Repositories(params string[] names)
    {
        var list = new RepositoryListOptions();
        foreach (var name in names)
        {
            list.Repositories.Add(new RepositoryOptions { Name = name });
        }

        return list;
    }

    public static IOptions<T> Wrap<T>(T value)
        where T : class => Options.Create(value);

    public static MutableOptionsMonitor<T> Monitor<T>(T value)
        where T : class => new(value);
}

/// <summary>An options monitor whose value a test can replace, like a reloaded file.</summary>
internal sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    where T : class
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
