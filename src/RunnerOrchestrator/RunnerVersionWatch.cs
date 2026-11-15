using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator;

/// <summary>
/// Once a day, warns when the configured runner image is older than the latest actions/runner
/// release: GitHub stops sending jobs to runners that are too old (#17). The runner-version
/// workflow opens the pull request that updates the image.
/// </summary>
internal sealed partial class RunnerVersionWatch(
    IGitHubRunnerApi api,
    IOptions<OrchestratorOptions> options,
    TimeProvider time,
    ILogger<RunnerVersionWatch> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await CheckAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        var image = options.Value.RunnerImage;
        if (ImageVersion(image) is not { } configured)
        {
            LogNoVersion(logger, image);
            return;
        }

        try
        {
            var latestText = await api.GetLatestRunnerVersionAsync(cancellationToken).ConfigureAwait(false);
            if (Version.TryParse(latestText, out var latest) && latest > configured)
            {
                LogOutdated(logger, configured, latest);
            }
        }
        catch (Exception exception) when (exception is GitHubApiException or HttpRequestException or JsonException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogCheckFailed(logger, exception.Message);
        }
    }

    /// <summary>The version in the image tag: <c>…runner:2.338.2</c> or <c>…runner:2.338.2@sha256:…</c>.</summary>
    internal static Version? ImageVersion(string image)
    {
        var reference = image.Split('@')[0];
        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        return colon > slash && Version.TryParse(reference[(colon + 1)..], out var version) ? version : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runner image is at {Configured} but actions/runner {Latest} is out; update the runner image before GitHub stops sending it jobs")]
    private static partial void LogOutdated(ILogger logger, Version configured, Version latest);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner image {Image} has no version tag; the runner version is not watched")]
    private static partial void LogNoVersion(ILogger logger, string image);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the latest runner release: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string reason);
}
