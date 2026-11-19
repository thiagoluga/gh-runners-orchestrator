using System.Net;
using System.Net.Sockets;
using RunnerOrchestrator.Pool;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests;

public sealed class StatusEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(20);

    [Fact]
    public void Healthz_NoCycleYet_Is503()
    {
        var (code, _, body) = StatusEndpoint.Respond("GET", "/healthz", new PoolStatusSnapshot(), Now, Poll);

        code.Should().Be(503);
        body.Should().Be("no cycle completed yet\n");
    }

    [Fact]
    public void Healthz_RecentCycle_Is200()
    {
        var snapshot = new PoolStatusSnapshot { LastSuccessfulCycleAt = Now - TimeSpan.FromSeconds(119) };

        StatusEndpoint.Respond("GET", "/healthz", snapshot, Now, Poll).Code.Should().Be(200);
    }

    [Fact]
    public void Healthz_LastCycleOlderThanThreeIntervalsAndAMinute_Is503()
    {
        var snapshot = new PoolStatusSnapshot { LastSuccessfulCycleAt = Now - TimeSpan.FromSeconds(121) };

        var (code, _, body) = StatusEndpoint.Respond("GET", "/healthz", snapshot, Now, Poll);

        code.Should().Be(503);
        body.Should().Be("no cycle completed recently\n");
    }

    [Theory]
    [InlineData("POST", "/healthz", 405)]
    [InlineData("GET", "/", 404)]
    [InlineData("GET", "/metrics/../healthz", 404)]
    [InlineData("HEAD", "/metrics", 200)]
    public void Respond_OnlyKnownReadsAnswer(string method, string path, int expected) =>
        StatusEndpoint.Respond(method, path, new PoolStatusSnapshot(), Now, Poll).Code.Should().Be(expected);

    [Fact]
    public void Metrics_RenderCountersGaugesAndLabels()
    {
        var snapshot = new PoolStatusSnapshot
        {
            Cycles = 12,
            CycleFailures = 1,
            LastSuccessfulCycleAt = Now,
            Slots = 2,
            BusySlots = 1,
            RunnersLaunched = 3,
            RunnersFinished = 2,
            Demand = [new RepositoryDemand(new RepositoryName("thiagoluga", "saas-foundry"), 4, 1, 1)],
            RateRemaining = new Dictionary<string, int?> { ["thiagoluga"] = 4800, ["maozinha"] = null },
        };

        var text = StatusEndpoint.RenderMetrics(snapshot, Now, Poll);

        text.Should().Contain("ghro_healthy 1\n")
            .And.Contain("ghro_cycles_total 12\n")
            .And.Contain("ghro_cycle_failures_total 1\n")
            .And.Contain("ghro_last_successful_cycle_timestamp_seconds " + Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n")
            .And.Contain("ghro_slots 2\n")
            .And.Contain("ghro_slots_busy 1\n")
            .And.Contain("ghro_runners_launched_total 3\n")
            .And.Contain("ghro_runners_finished_total 2\n")
            .And.Contain("ghro_queued_jobs{repository=\"thiagoluga/saas-foundry\"} 4\n")
            .And.Contain("ghro_waiting_runners{repository=\"thiagoluga/saas-foundry\"} 1\n")
            .And.Contain("ghro_rate_limit_remaining{account=\"thiagoluga\"} 4800\n")
            .And.Contain("# TYPE ghro_cycles_total counter\n")
            .And.NotContain("maozinha");
    }

    [Fact]
    public void Metrics_LabelValuesAreEscaped()
    {
        var snapshot = new PoolStatusSnapshot { RateRemaining = new Dictionary<string, int?> { ["a\"b\\c\nd"] = 1 } };

        StatusEndpoint.RenderMetrics(snapshot, Now, Poll).Should().Contain("{account=\"a\\\"b\\\\c\\nd\"} 1\n");
    }

    [Fact]
    public async Task Listener_ServesMetricsAndStopsWithTheHost()
    {
        var port = FreePort();
        var options = TestOptions.Orchestrator();
        options.StatusPort = port;
        options.StatusHost = "localhost";
        var status = new PoolStatus();
        status.CycleSucceeded(DateTimeOffset.UtcNow);
        var logger = new ListLogger<StatusEndpoint>();
        using var endpoint = new StatusEndpoint(status, TestOptions.Wrap(options), TimeProvider.System, logger);
        var cancellation = TestContext.Current.CancellationToken;

        await endpoint.StartAsync(cancellation);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/") };
            using var health = await client.GetAsync(new Uri("healthz", UriKind.Relative), cancellation);
            var metrics = await client.GetStringAsync(new Uri("metrics", UriKind.Relative), cancellation);

            health.StatusCode.Should().Be(HttpStatusCode.OK);
            metrics.Should().Contain("ghro_cycles_total 1\n");
            logger.AllText.Should().Contain($"listening on port {port}");
        }
        finally
        {
            await endpoint.StopAsync(cancellation);
        }
    }

    [Fact]
    public async Task Listener_PortZero_DoesNotListen()
    {
        var logger = new ListLogger<StatusEndpoint>();
        using var endpoint = new StatusEndpoint(new PoolStatus(), TestOptions.Wrap(TestOptions.Orchestrator()), TimeProvider.System, logger);

        await endpoint.StartAsync(TestContext.Current.CancellationToken);
        await endpoint.StopAsync(TestContext.Current.CancellationToken);

        logger.AllText.Should().BeEmpty();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
