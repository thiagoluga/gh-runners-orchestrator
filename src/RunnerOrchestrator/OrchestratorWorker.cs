using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator;

/// <summary>Runs <see cref="PoolCycle"/> every poll interval. A failed cycle is logged and the next one runs.</summary>
internal sealed partial class OrchestratorWorker(
    PoolCycle cycle,
    PoolStatus status,
    IOptions<OrchestratorOptions> options,
    TimeProvider time,
    ILogger<OrchestratorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.PollInterval;
        LogStarting(logger, interval, options.Value.Slots.Count, string.IsNullOrEmpty(options.Value.Host) ? "a single host" : $"host {options.Value.Host} of {string.Join(", ", options.Value.PoolHosts)}");
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                await cycle.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                status.CycleSucceeded(time.GetUtcNow());
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                status.CycleFailed(time.GetUtcNow());
                LogCycleFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Orchestrator polling every {Interval} with {Slots} slots on {Pool}")]
    private static partial void LogStarting(ILogger logger, TimeSpan interval, int slots, string pool);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cycle failed; the next one runs on schedule")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
