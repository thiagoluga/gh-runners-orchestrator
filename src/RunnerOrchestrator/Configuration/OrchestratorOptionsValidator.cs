using Microsoft.Extensions.Options;
using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.Configuration;

/// <summary>Fails the start on a configuration the pool cannot run safely with.</summary>
internal sealed class OrchestratorOptionsValidator : IValidateOptions<OrchestratorOptions>
{
    public ValidateOptionsResult Validate(string? name, OrchestratorOptions options)
    {
        var errors = new List<string>();
        ValidateAccounts(options, errors);

        if (options.PollInterval < TimeSpan.FromSeconds(5))
        {
            errors.Add("Orchestrator:PollInterval must be at least 5 seconds.");
        }

        if (options.IdleRunnerTimeout < TimeSpan.FromMinutes(1))
        {
            errors.Add("Orchestrator:IdleRunnerTimeout must be at least 1 minute.");
        }

        if (options.RunnerLabels.Count == 0 || options.RunnerLabels.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("Orchestrator:RunnerLabels needs at least one non-empty label.");
        }

        if (string.IsNullOrWhiteSpace(options.RunnerImage))
        {
            errors.Add("Orchestrator:RunnerImage is required.");
        }

        if (options.DockerSocketGroupId <= 0)
        {
            errors.Add("Orchestrator:DockerSocketGroupId is required (stat -c %g /var/run/docker.sock).");
        }

        ValidateApps(options, errors);

        if (options.StatusPort is < 0 or > 65535)
        {
            errors.Add("Orchestrator:StatusPort must be 0 (off) or a TCP port.");
        }

        if (options.StatusPort != 0 && (string.IsNullOrWhiteSpace(options.StatusHost) || options.StatusHost.Any(c => char.IsWhiteSpace(c) || c is '/' or '?' or '#' or '@')))
        {
            errors.Add("Orchestrator:StatusHost must be a host name, an address, + or *.");
        }

        ValidateSlots(options.Slots, errors);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateAccounts(OrchestratorOptions options, List<string> errors)
    {
        if (options.Accounts.Count == 0)
        {
            if (RepositoryName.TryParse($"{options.Owner}/repository") is null)
            {
                errors.Add("Orchestrator:Owner is required and must be a valid account name.");
            }

            return;
        }

        if (!string.IsNullOrEmpty(options.Owner))
        {
            errors.Add("Use either Orchestrator:Owner or Accounts, never both.");
        }

        if (options.Accounts.Any(a => RepositoryName.TryParse($"{a.Owner}/repository") is null || a.InstallationId <= 0))
        {
            errors.Add("Every account needs a valid Owner and a positive InstallationId.");
        }

        if (options.Accounts.Select(a => a.Owner).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Accounts.Count
            || options.Accounts.Select(a => a.InstallationId).Distinct().Count() != options.Accounts.Count)
        {
            errors.Add("Account owners and installation IDs must be unique.");
        }
    }

    private static void ValidateApps(OrchestratorOptions options, List<string> errors)
    {
        ValidateAppMode(options, errors);

        var apps = options.EffectiveApps().ToList();
        if (apps.Any(app => app.AppId <= 0 || string.IsNullOrWhiteSpace(app.PrivateKeyPath)))
        {
            errors.Add("Every GitHubApp needs a positive AppId and a PrivateKeyPath.");
        }

        if (apps.Select(app => app.AppId).Distinct().Count() != apps.Count)
        {
            errors.Add("GitHubApps AppId values must be unique.");
        }

        if (apps.Any(app => !app.ApiBaseUrl.IsAbsoluteUri || app.ApiBaseUrl.Scheme != Uri.UriSchemeHttps)
            || apps.Select(app => app.ApiBaseUrl).Distinct().Count() != 1)
        {
            errors.Add("All GitHubApps must use the same absolute HTTPS ApiBaseUrl.");
        }

        ValidateAppReferences(options, apps, errors);
    }

    private static void ValidateAppMode(OrchestratorOptions options, List<string> errors)
    {
        if (options.GitHubApps.Count > 0)
        {
            var defaults = new GitHubAppOptions();
            if (options.GitHubApp.AppId != defaults.AppId || options.GitHubApp.PrivateKeyPath != defaults.PrivateKeyPath
                || options.GitHubApp.ApiBaseUrl != defaults.ApiBaseUrl)
            {
                errors.Add("Use either GitHubApp or GitHubApps, never both.");
            }

            if (options.Accounts.Count == 0)
            {
                errors.Add("GitHubApps requires explicit Accounts with AppId references.");
            }
        }
    }

    private static void ValidateAppReferences(OrchestratorOptions options, List<GitHubAppOptions> apps, List<string> errors)
    {
        var registered = apps.Select(app => app.AppId).ToHashSet();
        var references = options.Accounts.Select(account => account.AppId == 0 && options.GitHubApps.Count == 0
            ? options.GitHubApp.AppId : account.AppId);
        if (references.Any(appId => !registered.Contains(appId)))
        {
            errors.Add("Every account AppId must reference a registered GitHubApp.");
        }
    }

    private static void ValidateSlots(IList<SlotOptions> slots, List<string> errors)
    {
        if (slots.Count == 0)
        {
            errors.Add("Orchestrator:Slots needs at least one slot.");
            return;
        }

        foreach (var slot in slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Name) || string.IsNullOrWhiteSpace(slot.Cpuset))
            {
                errors.Add("Every slot needs a Name and a Cpuset.");
            }

            if (!ByteSize.TryParse(slot.Memory, out _))
            {
                errors.Add($"Slot '{slot.Name}' has an invalid Memory value.");
            }
        }

        if (slots.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != slots.Count)
        {
            errors.Add("Slot names must be unique.");
        }
    }
}
