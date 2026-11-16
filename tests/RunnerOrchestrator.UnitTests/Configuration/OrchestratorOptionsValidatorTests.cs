using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void Validate_ExplicitAccounts_SucceedsWithTheSameGlobalSlots()
    {
        var options = TestOptions.Orchestrator();
        options.Owner = string.Empty;
        options.Accounts.Add(new AccountOptions { Owner = "thiagoluga", InstallationId = 42 });
        options.Accounts.Add(new AccountOptions { Owner = "maozinha", InstallationId = 84 });
        _validator.Validate(null, options).Succeeded.Should().BeTrue();
        options.Slots.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("thiagoluga", 84, false)]
    [InlineData("maozinha", 42, false)]
    [InlineData("maozinha", 0, false)]
    [InlineData("invalid/owner", 84, false)]
    [InlineData("maozinha", 84, true)]
    public void Validate_AmbiguousOrInvalidAccounts_Fails(string owner, long installation, bool legacyAlsoSet)
    {
        var options = TestOptions.Orchestrator();
        if (!legacyAlsoSet)
        {
            options.Owner = string.Empty;
        }

        options.Accounts.Add(new AccountOptions { Owner = "thiagoluga", InstallationId = 42 });
        options.Accounts.Add(new AccountOptions { Owner = owner, InstallationId = installation });
        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationBinding_LegacyAndExplicitAccounts_PreserveTheSameGlobalCapacity(bool explicitAccounts)
    {
        var original = TestOptions.Orchestrator();
        if (explicitAccounts)
        {
            original.Owner = string.Empty;
            original.Accounts.Add(new AccountOptions { Owner = "thiagoluga", InstallationId = 42 });
            original.Accounts.Add(new AccountOptions { Owner = "maozinha", InstallationId = 84 });
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Orchestrator = original })));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var bound = new OrchestratorOptions();
        configuration.GetSection(OrchestratorOptions.SectionName).Bind(bound);
        _validator.Validate(null, bound).Succeeded.Should().BeTrue();
        bound.Slots.Should().HaveCount(2);
        bound.AllowsOwner("THIAGOLUGA").Should().BeTrue();
        bound.AllowsOwner("maozinha").Should().Be(explicitAccounts);
    }
}
