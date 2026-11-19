# 0001. Detect queued jobs by polling the REST API

- Status: Accepted
- Date: 2026-11-15
- Deciders: Thiago Lugarini

## Context

The orchestrator must learn that a job is waiting for a self-hosted runner. GitHub offers
two ways:

- **Polling** the REST API: list workflow runs, then list the jobs of each run and keep the
  ones that are `queued` and whose labels the pool can serve.
- **The `workflow_job` webhook**, delivered by GitHub to a public HTTPS endpoint the moment
  a job is queued.

The first deployment is a VM on a laptop's Hyper-V, on a home network, with no public
address. A webhook would need a tunnel (Cloudflare Tunnel, smee.io or similar): one more
moving part, one more credential (the webhook secret) and one more failure mode (a tunnel
that is down means jobs that silently wait forever).

Jobs here take minutes (a full saas-foundry pull request is about 12 minutes). Picking a
job up 20 seconds later than a webhook would is not noticeable.

## Decision

Poll, per enabled repository, on a fixed interval (default 20 s, configurable):

1. `GET /repos/{owner}/{repo}/actions/runs?status=queued`,
   `GET /repos/{owner}/{repo}/actions/runs?status=pending` and
   `GET /repos/{owner}/{repo}/actions/runs?status=in_progress`. A run that already has one
   job running is `in_progress` while its later jobs (matrix legs, jobs behind `needs:`)
   are still `queued`; listing only queued runs would miss them. Job concurrency can also
   mark the run `pending` while another job is queued for a runner (observed 2026-11-16).
   Pending jobs themselves are never counted as runnable demand.
2. For each of those runs, `GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs?filter=latest`
   and keep jobs with `status == "queued"` whose `labels` are all served by the pool
   (case-insensitive subset match).
3. Every request sends `If-None-Match` with the last `ETag`. A `304 Not Modified` does
   not count against the rate limit, which is what keeps an idle pool almost free.

The poller only produces **demand** (how many matching jobs are queued per repository).
It never decides which job a runner gets: GitHub assigns a just-in-time runner to any
queued job of that repository with matching labels (see [0004](0004-ephemeral-runner-lifecycle.md)).

The detection sits behind an interface (`IJobDemandSource`), so the webhook can be added
later without touching the scheduler.

## Alternatives considered

- **Webhook `workflow_job`.** Faster and cheaper on API calls, but needs a public endpoint
  that the first deployment does not have. Deferred: tracked as a P5 issue in the roadmap.
- **A registered, idle runner per repository** (what personal accounts do today). This is
  the problem the project exists to remove.
- **Polling `GET /repos/{o}/{r}/actions/runners`** to see whether runners are busy. It says
  nothing about how many jobs are waiting.

## Consequences

- Rate limit budget. An installation token has at least 5,000 requests per hour. An idle
  repository costs three conditional requests per cycle, almost always `304`. A busy one
  adds one request per active run. With 8 repositories at 20 s that is 4,320 requests per
  hour in the worst case where nothing returns `304`; the orchestrator logs the remaining
  budget (`x-ratelimit-remaining`) and backs off when it drops under 10%.
- Latency of up to one interval between "queued" and "container starting".
- A job that is cancelled between two polls still causes a runner to be created; the idle
  timeout of [0004](0004-ephemeral-runner-lifecycle.md) removes it.
- Not verified yet: that `ETag`/`304` works on these list endpoints for an installation
  token (checked in the proof of concept), and that the `labels` field of a queued job
  holds exactly what `runs-on` asked for.
