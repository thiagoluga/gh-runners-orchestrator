using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator;

/// <summary>
/// Optional plain HTTP endpoint for a watchdog or a dashboard (#22): <c>GET /healthz</c> and
/// <c>GET /metrics</c> (Prometheus text format). Off unless <c>Orchestrator:StatusPort</c> is set;
/// the reference deployment publishes it on the host's loopback only. It answers counters and
/// repository names, never a token, a key or a runner configuration, and accepts nothing.
/// A failure to listen is logged and leaves the pool running.
/// </summary>
internal sealed partial class StatusEndpoint(
    PoolStatus status,
    IOptions<OrchestratorOptions> options,
    TimeProvider time,
    ILogger<StatusEndpoint> logger) : BackgroundService
{
    private const string PlainText = "text/plain; charset=utf-8";
    private const string Gauge = "gauge";
    private const string Counter = "counter";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (settings.StatusPort == 0)
        {
            return;
        }

        using var listener = new HttpListener();
        try
        {
            // Plain HTTP on purpose: read-only counters, published on the host's loopback only.
            listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://{settings.StatusHost}:{settings.StatusPort}/")); // NOSONAR S5332
            listener.Start();
        }
        catch (Exception exception) when (exception is HttpListenerException or ArgumentException)
        {
            LogListenFailed(logger, settings.StatusPort, exception.Message);
            return;
        }

        LogListening(logger, settings.StatusPort);
        using var registration = stoppingToken.Register(listener.Stop);
        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException or IOException)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    // Never let the optional endpoint stop the host; the pool keeps running.
                    LogListenerStopped(logger, exception.Message);
                }

                break;
            }

            Serve(context);
        }
    }

    private void Serve(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            var (code, contentType, body) = Respond(request.HttpMethod, request.Url?.AbsolutePath ?? string.Empty,
                status.Snapshot(), time.GetUtcNow(), options.Value.PollInterval);
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = code;
            response.ContentType = contentType;
            response.Headers["Cache-Control"] = "no-store";
            response.ContentLength64 = bytes.Length;
            if (!string.Equals(request.HttpMethod, "HEAD", StringComparison.Ordinal))
            {
                response.OutputStream.Write(bytes);
            }
        }
        catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The client went away; nothing to answer.
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
            {
                // Already closed by the client.
            }
        }
    }

    /// <summary>Healthy while the last cycle that completed did so within three poll intervals plus a minute.</summary>
    public static bool IsHealthy(PoolStatusSnapshot snapshot, DateTimeOffset now, TimeSpan pollInterval) =>
        snapshot.LastSuccessfulCycleAt is { } last && now - last <= (pollInterval * 3) + TimeSpan.FromMinutes(1);

    public static (int Code, string ContentType, string Body) Respond(string method, string path, PoolStatusSnapshot snapshot, DateTimeOffset now, TimeSpan pollInterval)
    {
        if (method is not ("GET" or "HEAD"))
        {
            return (405, PlainText, "method not allowed\n");
        }

        return path switch
        {
            "/healthz" => Health(snapshot, now, pollInterval),
            "/metrics" => (200, "text/plain; version=0.0.4; charset=utf-8", RenderMetrics(snapshot, now, pollInterval)),
            _ => (404, PlainText, "not found\n"),
        };
    }

    private static (int Code, string ContentType, string Body) Health(PoolStatusSnapshot snapshot, DateTimeOffset now, TimeSpan pollInterval)
    {
        if (IsHealthy(snapshot, now, pollInterval))
        {
            return (200, PlainText, "ok\n");
        }

        var reason = snapshot.LastSuccessfulCycleAt is null ? "no cycle completed yet\n" : "no cycle completed recently\n";
        return (503, PlainText, reason);
    }

    public static string RenderMetrics(PoolStatusSnapshot snapshot, DateTimeOffset now, TimeSpan pollInterval)
    {
        var text = new StringBuilder();
        Metric(text, "ghro_healthy", Gauge, "1 while cycles complete on schedule.", IsHealthy(snapshot, now, pollInterval) ? 1 : 0);
        Metric(text, "ghro_cycles_total", Counter, "Cycles run since start.", snapshot.Cycles);
        Metric(text, "ghro_cycle_failures_total", Counter, "Cycles that ended with an error.", snapshot.CycleFailures);
        if (snapshot.LastSuccessfulCycleAt is { } last)
        {
            Metric(text, "ghro_last_successful_cycle_timestamp_seconds", Gauge, "Unix time of the last cycle that completed.", last.ToUnixTimeSeconds());
        }

        Metric(text, "ghro_rate_limited", Gauge, "1 while demand is skipped for a low GitHub API budget.", snapshot.RateLimited ? 1 : 0);
        Metric(text, "ghro_blocked", Gauge, "1 while a managed container without a configured slot stops launches.", snapshot.Blocked ? 1 : 0);
        Metric(text, "ghro_slots", Gauge, "Configured slots.", snapshot.Slots);
        Metric(text, "ghro_slots_busy", Gauge, "Slots holding a runner container.", snapshot.BusySlots);
        Metric(text, "ghro_runners_launched_total", Counter, "Runner containers started since start.", snapshot.RunnersLaunched);
        Metric(text, "ghro_runners_finished_total", Counter, "Finished runner containers removed since start.", snapshot.RunnersFinished);
        Metric(text, "ghro_launch_failures_total", Counter, "Launches that failed since start.", snapshot.LaunchFailures);

        Header(text, "ghro_queued_jobs", Gauge, "Queued jobs the pool serves, per repository.");
        foreach (var demand in snapshot.Demand)
        {
            Sample(text, "ghro_queued_jobs", "repository", demand.Repository.ToString(), demand.QueuedJobs);
        }

        Header(text, "ghro_waiting_runners", Gauge, "Runners started but not busy yet, per repository.");
        foreach (var demand in snapshot.Demand)
        {
            Sample(text, "ghro_waiting_runners", "repository", demand.Repository.ToString(), demand.WaitingRunners);
        }

        Header(text, "ghro_rate_limit_remaining", Gauge, "Remaining GitHub API requests, per account.");
        foreach (var (owner, remaining) in snapshot.RateRemaining.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (remaining is { } value)
            {
                Sample(text, "ghro_rate_limit_remaining", "account", owner, value);
            }
        }

        return text.ToString();
    }

    private static void Metric(StringBuilder text, string name, string type, string help, long value)
    {
        Header(text, name, type, help);
        text.Append(CultureInfo.InvariantCulture, $"{name} {value}\n");
    }

    private static void Header(StringBuilder text, string name, string type, string help) =>
        text.Append(CultureInfo.InvariantCulture, $"# HELP {name} {help}\n# TYPE {name} {type}\n");

    private static void Sample(StringBuilder text, string name, string label, string value, long sample) =>
        text.Append(CultureInfo.InvariantCulture, $"{name}{{{label}=\"{Escape(value)}\"}} {sample}\n");

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Information, Message = "Status endpoint listening on port {Port}")]
    private static partial void LogListening(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Error, Message = "Status endpoint stopped: {Reason}; the pool keeps running without it")]
    private static partial void LogListenerStopped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Status endpoint could not listen on port {Port}: {Reason}; the pool keeps running without it")]
    private static partial void LogListenFailed(ILogger logger, int port, string reason);
}
