using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests;

public sealed class RunnerVersionWatchTests
{
    private readonly IGitHubRunnerApi _api = Substitute.For<IGitHubRunnerApi>();
    private readonly ListLogger<RunnerVersionWatch> _logger = new();

    [Theory]
    [InlineData("ghcr.io/thiagoluga/gh-runners-orchestrator-runner:2.338.2", "2.338.2")]
    [InlineData("ghcr.io/thiagoluga/runner:2.338.2@sha256:abc", "2.338.2")]
    [InlineData("localhost:5000/runner:2.1.0", "2.1.0")]
    public void ImageVersion_VersionTag_IsRead(string image, string expected)
    {
        RunnerVersionWatch.ImageVersion(image).Should().Be(Version.Parse(expected));
    }

    [Theory]
    [InlineData("ghcr.io/thiagoluga/runner:main")]
    [InlineData("localhost:5000/runner")]
    [InlineData("poc/runner:local")]
    public void ImageVersion_NoVersionTag_IsNull(string image)
    {
        RunnerVersionWatch.ImageVersion(image).Should().BeNull();
    }

    [Fact]
    public async Task Check_NewerReleaseOut_Warns()
    {
        _api.GetLatestRunnerVersionAsync(Arg.Any<CancellationToken>()).Returns("2.339.0");

        await Create("ghcr.io/x/runner:2.338.2").CheckAsync(TestContext.Current.CancellationToken);

        _logger.AllText.Should().Contain("Warning").And.Contain("2.338.2").And.Contain("2.339.0");
    }

    [Fact]
    public async Task Check_SameRelease_StaysQuiet()
    {
        _api.GetLatestRunnerVersionAsync(Arg.Any<CancellationToken>()).Returns("2.338.2");

        await Create("ghcr.io/x/runner:2.338.2").CheckAsync(TestContext.Current.CancellationToken);

        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_GitHubUnreachable_LogsAndContinues()
    {
        _api.GetLatestRunnerVersionAsync(Arg.Any<CancellationToken>()).Returns<string>(_ => throw new HttpRequestException("no route"));

        await Create("ghcr.io/x/runner:2.338.2").CheckAsync(TestContext.Current.CancellationToken);

        _logger.AllText.Should().Contain("Could not read the latest runner release: no route");
    }

    private RunnerVersionWatch Create(string image)
    {
        var options = TestOptions.Orchestrator();
        options.RunnerImage = image;
        return new RunnerVersionWatch(_api, TestOptions.Wrap(options), TimeProvider.System, _logger);
    }
}
