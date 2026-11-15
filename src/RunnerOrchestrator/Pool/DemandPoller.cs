using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// Reads the queued jobs the pool can serve in one repository (ADR 0001). Both queued and
/// in-progress runs are read: a run with one job running is in progress while its later jobs
/// still wait.
/// </summary>
internal sealed class DemandPoller(IGitHubRunnerApi api, IOptions<OrchestratorOptions> options)
{
    private readonly IReadOnlyList<string> _served = ServedLabels.For(options.Value.RunnerLabels);

    public async Task<QueuedJobs> ReadQueuedJobsAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        var queuedRuns = await api.ListRunIdsAsync(repository, "queued", cancellationToken).ConfigureAwait(false);
        var activeRuns = await api.ListRunIdsAsync(repository, "in_progress", cancellationToken).ConfigureAwait(false);
        var count = 0;
        DateTimeOffset? oldest = null;
        foreach (var runId in queuedRuns.Concat(activeRuns).Distinct())
        {
            var jobs = await api.ListJobsAsync(repository, runId, cancellationToken).ConfigureAwait(false);
            foreach (var job in jobs.Where(job => job.Status == "queued" && ServedLabels.Serves(_served, job.Labels)))
            {
                count++;
                if (job.CreatedAt is { } created && (oldest is null || created < oldest))
                {
                    oldest = created;
                }
            }
        }

        return new QueuedJobs(count, oldest);
    }
}

/// <summary>How many served jobs wait, and since when the oldest one waits (for the fairness tie-break).</summary>
internal sealed record QueuedJobs(int Count, DateTimeOffset? OldestQueuedAt);
