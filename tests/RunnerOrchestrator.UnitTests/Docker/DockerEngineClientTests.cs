using System.Net;
using System.Text.Json;
using RunnerOrchestrator.Docker;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests.Docker;

public sealed class DockerEngineClientTests : IDisposable
{
    private readonly FakeHttpHandler _handler = new();

    [Fact]
    public async Task Create_SendsCpusetMemoryNofileHostNetworkSocketAndGroup_WithoutCpuQuota()
    {
        _handler.On(HttpMethod.Post, "/v1.47/containers/create?name=orch-x", HttpStatusCode.Created, """{"Id":"c1","Warnings":[]}""");
        using var client = Create();
        var spec = new ContainerSpec(
            "orch-x",
            "ghcr.io/thiagoluga/runner:1",
            new Dictionary<string, string> { ["orchestrator.managed"] = "true" },
            new Dictionary<string, string> { ["ACTIONS_RUNNER_INPUT_JITCONFIG"] = "SECRET" },
            "0-4",
            17179869184,
            65536,
            "/var/run/docker.sock",
            988);

        var id = await client.CreateAsync(spec, TestContext.Current.CancellationToken);

        id.Should().Be("c1");
        spec.ToString().Should().NotContain("SECRET");
        using var body = JsonDocument.Parse(_handler.Requests.Single().Body!);
        var root = body.RootElement;
        root.GetProperty("Image").GetString().Should().Be("ghcr.io/thiagoluga/runner:1");
        root.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).Should().Equal("ACTIONS_RUNNER_INPUT_JITCONFIG=SECRET");
        root.GetProperty("Labels").GetProperty("orchestrator.managed").GetString().Should().Be("true");
        var host = root.GetProperty("HostConfig");
        host.GetProperty("CpusetCpus").GetString().Should().Be("0-4");
        host.GetProperty("Memory").GetInt64().Should().Be(17179869184);
        host.GetProperty("MemorySwap").GetInt64().Should().Be(17179869184);
        host.GetProperty("NetworkMode").GetString().Should().Be("host");
        host.GetProperty("Binds").EnumerateArray().Select(e => e.GetString()).Should().Equal("/var/run/docker.sock:/var/run/docker.sock");
        host.GetProperty("GroupAdd").EnumerateArray().Select(e => e.GetString()).Should().Equal("988");
        host.GetProperty("Ulimits")[0].GetProperty("Soft").GetInt64().Should().Be(65536);
        host.GetProperty("AutoRemove").GetBoolean().Should().BeFalse();
        host.TryGetProperty("CpuQuota", out _).Should().BeFalse();
        host.TryGetProperty("NanoCpus", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Create_EngineRefuses_MessageHasPathButNotEnvironment()
    {
        _handler.On(HttpMethod.Post, "/v1.47/containers/create", HttpStatusCode.Conflict, """{"message":"Conflict. The container name \"/orch-x\" is already in use"}""");
        using var client = Create();
        var spec = new ContainerSpec("orch-x", "img", new Dictionary<string, string>(), new Dictionary<string, string> { ["X"] = "SECRET" }, "0", 1, 1, "/s", 1);

        var act = () => client.CreateAsync(spec, TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<DockerEngineException>();
        thrown.Which.Message.Should().Contain("409").And.Contain("already in use").And.NotContain("SECRET");
    }

    [Fact]
    public async Task ListByLabel_FiltersByLabelAndMapsState()
    {
        _handler.On(HttpMethod.Get, "/v1.47/containers/json", HttpStatusCode.OK,
            """[{"Id":"c1","Names":["/orch-x"],"State":"exited","Labels":{"orchestrator.slot":"slot-1"}}]""");
        using var client = Create();

        var containers = await client.ListByLabelAsync("orchestrator.managed=true", TestContext.Current.CancellationToken);

        containers.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = "c1", Name = "orch-x", State = "exited" });
        containers[0].Labels["orchestrator.slot"].Should().Be("slot-1");
        var query = Uri.UnescapeDataString(_handler.Requests.Single().Uri.Query);
        query.Should().Contain("all=true").And.Contain("""{"label":["orchestrator.managed=true"]}""");
    }

    [Fact]
    public async Task Inspect_ReadsExitCodeAndTimes_NeverStartedIsNull()
    {
        _handler.On(HttpMethod.Get, "/v1.47/containers/c1/json", HttpStatusCode.OK,
            """{"Id":"c1","State":{"Status":"exited","ExitCode":3,"StartedAt":"2026-11-15T12:00:00.5Z","FinishedAt":"0001-01-01T00:00:00Z"}}""");
        using var client = Create();

        var state = await client.InspectAsync("c1", TestContext.Current.CancellationToken);

        state.ExitCode.Should().Be(3);
        state.StartedAt.Should().Be(new DateTimeOffset(2026, 11, 15, 12, 0, 0, 500, TimeSpan.Zero));
        state.FinishedAt.Should().BeNull();
    }

    [Fact]
    public async Task Remove_RemovesVolumesAndToleratesMissingContainer()
    {
        _handler.On(HttpMethod.Delete, "/v1.47/containers/c1", HttpStatusCode.NotFound, """{"message":"No such container"}""");
        using var client = Create();

        await client.RemoveAsync("c1", TestContext.Current.CancellationToken);

        _handler.Requests.Single().Uri.Query.Should().Contain("v=true");
    }

    [Fact]
    public async Task EnsureImage_Present_DoesNotPull()
    {
        _handler.On(HttpMethod.Get, "/v1.47/images/ghcr.io/thiagoluga/runner:1/json", HttpStatusCode.OK, "{}");
        using var client = Create();

        await client.EnsureImageAsync("ghcr.io/thiagoluga/runner:1", TestContext.Current.CancellationToken);

        _handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureImage_PullReportsErrorInStream_Throws()
    {
        _handler
            .On(HttpMethod.Get, "/v1.47/images/", HttpStatusCode.NotFound, """{"message":"No such image"}""")
            .On(HttpMethod.Post, "/v1.47/images/create", HttpStatusCode.OK, "{\"status\":\"Pulling\"}\n{\"error\":\"manifest unknown\"}\n");
        using var client = Create();

        var act = () => client.EnsureImageAsync("ghcr.io/thiagoluga/runner:missing", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<DockerEngineException>()).WithMessage("*manifest unknown*");
    }

    private DockerEngineClient Create() => new(DockerEngineClient.CreateHttpClient(_handler));

    public void Dispose() => _handler.Dispose();
}
