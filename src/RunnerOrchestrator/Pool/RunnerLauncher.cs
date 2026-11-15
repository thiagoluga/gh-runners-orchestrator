using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.Docker;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// Creates one just-in-time runner in one slot (ADR 0004). The JIT configuration goes only
/// into the container environment, where the runner reads and clears it; it is never logged,
/// labelled or put on a command line.
/// </summary>
internal sealed partial class RunnerLauncher(
    IGitHubRunnerApi api,
    IContainerRuntime runtime,
    IOptions<OrchestratorOptions> options,
    TimeProvider time,
    ILogger<RunnerLauncher> logger)
{
    /// <summary>Environment variable the runner reads its <c>--jitconfig</c> argument from.</summary>
    public const string JitConfigVariable = "ACTIONS_RUNNER_INPUT_JITCONFIG";

    /// <summary>Measured on saas-foundry: 1024 kept Aspire hosts from starting.</summary>
    public const int NofileLimit = 65536;

    private const int MaxNameLength = 64;

    public async Task LaunchAsync(RepositoryName repository, SlotOptions slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var settings = options.Value;
        if (!ByteSize.TryParse(slot.Memory, out var memory))
        {
            throw new InvalidOperationException($"Slot {slot.Name} has an invalid memory limit.");
        }

        await runtime.EnsureImageAsync(settings.RunnerImage, cancellationToken).ConfigureAwait(false);

        var name = CreateRunnerName(repository, slot.Name, RandomNumberGenerator.GetHexString(8, lowercase: true));
        var jit = await api.GenerateJitConfigAsync(repository, name, ServedLabels.For(settings.RunnerLabels), cancellationToken).ConfigureAwait(false);
        string? containerId = null;
        try
        {
            var spec = new ContainerSpec(
                Name: name,
                Image: settings.RunnerImage,
                Labels: new Dictionary<string, string>
                {
                    [ManagedLabels.Managed] = "true",
                    [ManagedLabels.Repository] = repository.ToString(),
                    [ManagedLabels.Slot] = slot.Name,
                    [ManagedLabels.RunnerName] = jit.RunnerName,
                    [ManagedLabels.RunnerId] = jit.RunnerId.ToString(CultureInfo.InvariantCulture),
                    [ManagedLabels.CreatedAt] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                },
                Environment: new Dictionary<string, string> { [JitConfigVariable] = jit.EncodedJitConfig },
                Cpuset: slot.Cpuset,
                MemoryBytes: memory,
                NofileLimit: NofileLimit,
                DockerSocket: settings.DockerSocket,
                DockerSocketGroupId: settings.DockerSocketGroupId);
            containerId = await runtime.CreateAsync(spec, cancellationToken).ConfigureAwait(false);
            await runtime.StartAsync(containerId, cancellationToken).ConfigureAwait(false);
            LogLaunched(logger, jit.RunnerName, jit.RunnerId, repository, slot.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogLaunchFailed(logger, jit.RunnerName, repository, slot.Name, exception.Message);
            await UndoAsync(repository, jit, containerId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary><c>orch-&lt;repo&gt;-&lt;slot&gt;-&lt;random&gt;</c>, lower case, valid as a runner and a container name.</summary>
    internal static string CreateRunnerName(RepositoryName repository, string slot, string suffix)
    {
        var tail = $"-{Sanitize(slot)}-{suffix}";
        var repo = Sanitize(repository.Name);
        var room = MaxNameLength - "orch-".Length - tail.Length;
        if (repo.Length > room)
        {
            repo = repo[..Math.Max(1, room)].TrimEnd('-');
        }

        return $"orch-{repo}{tail}";
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        return builder.ToString().Trim('-');
    }

    private async Task UndoAsync(RepositoryName repository, JitRunner jit, string? containerId)
    {
        // Best effort, without the caller's token: a half-made runner must not hold a slot or a registration.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (containerId is not null)
            {
                await runtime.RemoveAsync(containerId, timeout.Token).ConfigureAwait(false);
            }

            await api.DeleteRunnerAsync(repository, jit.RunnerId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is GitHubApiException or DockerEngineException or HttpRequestException or OperationCanceledException)
        {
            LogUndoFailed(logger, jit.RunnerName, exception.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner {RunnerName} ({RunnerId}) started for {Repository} in {Slot}")]
    private static partial void LogLaunched(ILogger logger, string runnerName, long runnerId, RepositoryName repository, string slot);

    [LoggerMessage(Level = LogLevel.Error, Message = "Runner {RunnerName} for {Repository} in {Slot} could not start: {Reason}")]
    private static partial void LogLaunchFailed(ILogger logger, string runnerName, RepositoryName repository, string slot, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleaning up runner {RunnerName} after a failed start did not finish: {Reason}")]
    private static partial void LogUndoFailed(ILogger logger, string runnerName, string reason);
}
