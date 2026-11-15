using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.UnitTests.Support;

namespace RunnerOrchestrator.UnitTests.Configuration;

public sealed class OrchestratorOptionsValidatorTests
{
    private readonly OrchestratorOptionsValidator _validator = new();

    [Fact]
    public void Validate_CompleteOptions_Succeeds()
    {
        _validator.Validate(null, TestOptions.Orchestrator()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyOptions_ListsEveryMissingValue()
    {
        var result = _validator.Validate(null, new OrchestratorOptions());

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("Owner", StringComparison.Ordinal))
            .And.Contain(f => f.Contains("RunnerLabels", StringComparison.Ordinal))
            .And.Contain(f => f.Contains("RunnerImage", StringComparison.Ordinal))
            .And.Contain(f => f.Contains("DockerSocketGroupId", StringComparison.Ordinal))
            .And.Contain(f => f.Contains("AppId", StringComparison.Ordinal))
            .And.Contain(f => f.Contains("Slots", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DuplicateSlotOrBadMemory_Fails()
    {
        var options = TestOptions.Orchestrator();
        options.Slots.Add(new SlotOptions { Name = "SLOT-1", Cpuset = "10-11", Memory = "lots" });

        var result = _validator.Validate(null, options);

        result.Failures.Should().Contain(f => f.Contains("unique", StringComparison.Ordinal))
            .And.Contain(f => f.Contains("invalid Memory", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PollIntervalTooShort_Fails()
    {
        var options = TestOptions.Orchestrator();
        options.PollInterval = TimeSpan.FromSeconds(1);

        _validator.Validate(null, options).Failures.Should().ContainSingle(f => f.Contains("PollInterval", StringComparison.Ordinal));
    }
}
