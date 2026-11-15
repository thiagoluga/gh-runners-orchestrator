using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.UnitTests.Pool;

public sealed class ValueParsingTests
{
    [Theory]
    [InlineData("16g", 17179869184L)]
    [InlineData("512m", 536870912L)]
    [InlineData("4K", 4096L)]
    [InlineData("1000", 1000L)]
    public void ByteSize_ValidValue_ReturnsBytes(string value, long expected)
    {
        ByteSize.TryParse(value, out var bytes).Should().BeTrue();
        bytes.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("g")]
    [InlineData("-1g")]
    [InlineData("0")]
    [InlineData("16 gb")]
    public void ByteSize_InvalidValue_ReturnsFalse(string? value)
    {
        ByteSize.TryParse(value, out _).Should().BeFalse();
    }

    [Fact]
    public void RepositoryName_Parse_ComparesWithoutCase()
    {
        RepositoryName.Parse("ThiagoLuga/Runner-Pool_Sandbox.x").Should().Be(new RepositoryName("thiagoluga", "runner-pool_sandbox.X"));
    }

    [Theory]
    [InlineData("thiagoluga")]
    [InlineData("thiagoluga/a/b")]
    [InlineData("/repo")]
    [InlineData("owner/repo?x=1")]
    [InlineData("owner/../x")]
    [InlineData("owner/..")]
    [InlineData("./repo")]
    public void RepositoryName_Parse_RejectsAnythingButOwnerSlashName(string value)
    {
        var parse = () => RepositoryName.Parse(value);

        parse.Should().Throw<FormatException>();
    }

    [Fact]
    public void ServedLabels_JobAskingOnlyForPoolLabels_IsServed()
    {
        var served = ServedLabels.For(["vm-pool"]);

        ServedLabels.Serves(served, ["self-hosted", "VM-POOL"]).Should().BeTrue();
        ServedLabels.Serves(served, ["vm-pool"]).Should().BeTrue();
    }

    [Fact]
    public void ServedLabels_JobAskingForAnotherLabel_IsNotServed()
    {
        var served = ServedLabels.For(["vm-pool"]);

        ServedLabels.Serves(served, ["ubuntu-24.04"]).Should().BeFalse();
        ServedLabels.Serves(served, ["vm-pool", "gpu"]).Should().BeFalse();
        ServedLabels.Serves(served, []).Should().BeFalse();
    }

    [Fact]
    public void CreateRunnerName_LongRepository_StaysWithin64ValidCharacters()
    {
        var name = RunnerLauncher.CreateRunnerName(new RepositoryName("thiagoluga", new string('a', 90) + ".X_y"), "slot-1", "0123abcd");

        name.Should().StartWith("orch-aaa").And.EndWith("-slot-1-0123abcd");
        name.Length.Should().BeLessThanOrEqualTo(64);
        name.Should().MatchRegex("^[a-z0-9-]+$");
    }

    [Fact]
    public void CreateRunnerName_ShortRepository_KeepsItWhole()
    {
        RunnerLauncher.CreateRunnerName(new RepositoryName("thiagoluga", "Runner.Pool_Sandbox"), "slot-2", "ffff0000")
            .Should().Be("orch-runner-pool-sandbox-slot-2-ffff0000");
    }
}
