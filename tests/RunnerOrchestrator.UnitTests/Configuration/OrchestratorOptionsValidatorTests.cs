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

    [Theory]
    [InlineData(0, "", true)]
    [InlineData(9464, "+", true)]
    [InlineData(9464, "127.0.0.1", true)]
    [InlineData(-1, "+", false)]
    [InlineData(65536, "+", false)]
    [InlineData(9464, "", false)]
    [InlineData(9464, "evil.example/path", false)]
    [InlineData(9464, "user@host", false)]
    public void Validate_StatusEndpoint(int port, string host, bool valid)
    {
        var options = TestOptions.Orchestrator();
        options.StatusPort = port;
        options.StatusHost = host;

        _validator.Validate(null, options).Succeeded.Should().Be(valid);
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

    [Theory]
    [InlineData("valid")]
    [InlineData("duplicate-app")]
    [InlineData("missing-reference")]
    [InlineData("unknown-reference")]
    [InlineData("mixed-legacy")]
    [InlineData("missing-key-path")]
    [InlineData("legacy-owner")]
    [InlineData("different-api")]
    [InlineData("http-api")]
    [InlineData("invalid-app-id")]
    public void Validate_AppRegistry_RejectsAmbiguityAndInvalidReferences(string scenario)
    {
        var options = TestOptions.Orchestrator();
        options.Owner = string.Empty;
        options.GitHubApp = new GitHubAppOptions();
        options.GitHubApps.Add(new GitHubAppOptions { AppId = 12345, PrivateKeyPath = "/run/secrets/one.pem" });
        options.GitHubApps.Add(new GitHubAppOptions { AppId = 67890, PrivateKeyPath = "/run/secrets/two.pem" });
        options.Accounts.Add(new AccountOptions { Owner = "thiagoluga", InstallationId = 42, AppId = 12345 });
        options.Accounts.Add(new AccountOptions { Owner = "maozinha", InstallationId = 84, AppId = 67890 });
        switch (scenario)
        {
            case "duplicate-app": options.GitHubApps[1].AppId = 12345; break;
            case "missing-reference": options.Accounts[1].AppId = 0; break;
            case "unknown-reference": options.Accounts[1].AppId = 999; break;
            case "mixed-legacy": options.GitHubApp.AppId = 12345; break;
            case "missing-key-path": options.GitHubApps[1].PrivateKeyPath = " "; break;
            case "legacy-owner": options.Accounts.Clear(); options.Owner = "thiagoluga"; break;
            case "different-api": options.GitHubApps[1].ApiBaseUrl = new Uri("https://other.example/"); break;
            case "http-api": options.GitHubApps[1].ApiBaseUrl = new Uri("http://api.github.com/"); break;
            case "invalid-app-id": options.GitHubApps[1].AppId = 0; break;
        }

        _validator.Validate(null, options).Succeeded.Should().Be(scenario == "valid");
    }

    [Fact]
    public void Validate_LegacySingleApp_RejectsExplicitAccountReferenceToAnotherApp()
    {
        var options = TestOptions.Orchestrator();
        options.Owner = string.Empty;
        options.Accounts.Add(new AccountOptions { Owner = "thiagoluga", InstallationId = 42, AppId = 999 });
        _validator.Validate(null, options).Failed.Should().BeTrue();
        options.Accounts[0].AppId = options.GitHubApp.AppId;
        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void ConfigurationBinding_AppRegistry_ResolvesEachAccountAndKeepsTwoSlots()
    {
        var original = TestOptions.Orchestrator();
        original.Owner = string.Empty;
        original.GitHubApp = new GitHubAppOptions();
        original.GitHubApps.Add(new GitHubAppOptions { AppId = 12345, PrivateKeyPath = "/run/secrets/one.pem" });
        original.GitHubApps.Add(new GitHubAppOptions { AppId = 67890, PrivateKeyPath = "/run/secrets/two.pem" });
        original.Accounts.Add(new AccountOptions { Owner = "thiagoluga", InstallationId = 42, AppId = 12345 });
        original.Accounts.Add(new AccountOptions { Owner = "maozinha", InstallationId = 84, AppId = 67890 });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Orchestrator = original })));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var bound = new OrchestratorOptions();
        configuration.GetSection(OrchestratorOptions.SectionName).Bind(bound);
        _validator.Validate(null, bound).Succeeded.Should().BeTrue();
        bound.AppForOwner("MAOZINHA").PrivateKeyPath.Should().Be("/run/secrets/two.pem");
        bound.Slots.Should().HaveCount(2);
    }
}
