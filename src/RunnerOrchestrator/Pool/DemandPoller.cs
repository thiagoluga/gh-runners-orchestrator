using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.Pool;

/// <summary>
/// Reads served queued jobs in queued, pending and in-progress runs (ADR 0001). A run can
/// be pending on job concurrency while another job is already queued for a runner.
/// </summary>
internal sealed class DemandPoller(IGitHubRunnerApi api, IOptions<OrchestratorOptions> options)
{
    private readonly IReadOnlyList<string> _served = ServedLabels.For(options.Value.RunnerLabels);

    public async Task<QueuedJobs> ReadQueuedJobsAsync(RepositoryName repository, CancellationToken cancellationToken)
    {
        var queuedRuns = await api.ListRunIdsAsync(repository, "queued", cancellationToken).ConfigureAwait(false);
        var activeRuns = await api.ListRunIdsAsync(repository, "in_progress", cancellationToken).ConfigureAwait(false);
        var pendingRuns = await api.ListRunIdsAsync(repository, "pending", cancellationToken).ConfigureAwait(false);
        var served = new List<WorkflowJob>();
        DateTimeOffset? oldest = null;
        foreach (var runId in queuedRuns.Concat(activeRuns).Concat(pendingRuns).Distinct())
        {
            var jobs = await api.ListJobsAsync(repository, runId, cancellationToken).ConfigureAwait(false);
            foreach (var job in jobs.Where(job => job.Status == "queued" && ServedLabels.Serves(_served, job.Labels)))
            {
                served.Add(job);
                if (job.CreatedAt is { } created && (oldest is null || created < oldest))
                {
                    oldest = created;
                }
            }
        }

        return new QueuedJobs(served, oldest);
    }
}

/// <summary>The served jobs that wait, and since when the oldest one waits (for the fairness tie-break).</summary>
internal sealed record QueuedJobs(IReadOnlyList<WorkflowJob> Jobs, DateTimeOffset? OldestQueuedAt)
{
    public int Count => Jobs.Count;
}
