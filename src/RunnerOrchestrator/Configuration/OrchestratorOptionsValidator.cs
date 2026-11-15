using Microsoft.Extensions.Options;
using RunnerOrchestrator.Pool;

namespace RunnerOrchestrator.Configuration;

/// <summary>Fails the start on a configuration the pool cannot run safely with.</summary>
internal sealed class OrchestratorOptionsValidator : IValidateOptions<OrchestratorOptions>
{
    public ValidateOptionsResult Validate(string? name, OrchestratorOptions options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Owner))
        {
            errors.Add("Orchestrator:Owner is required.");
        }

        if (options.PollInterval < TimeSpan.FromSeconds(5))
        {
            errors.Add("Orchestrator:PollInterval must be at least 5 seconds.");
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

        if (options.GitHubApp.AppId <= 0)
        {
            errors.Add("Orchestrator:GitHubApp:AppId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.GitHubApp.PrivateKeyPath))
        {
            errors.Add("Orchestrator:GitHubApp:PrivateKeyPath is required.");
        }

        ValidateSlots(options.Slots, errors);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
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
