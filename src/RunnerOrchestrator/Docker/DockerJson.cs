using System.Text.Json.Serialization;

namespace RunnerOrchestrator.Docker;

// Wire shapes of the Docker Engine API (PascalCase on the wire, like the property names).

internal sealed record CreateContainerRequest(
    string Image,
    IReadOnlyList<string> Env,
    IReadOnlyDictionary<string, string> Labels,
    HostConfigDto HostConfig);

internal sealed record HostConfigDto(
    string CpusetCpus,
    long Memory,
    long MemorySwap,
    string NetworkMode,
    IReadOnlyList<string> Binds,
    IReadOnlyList<string> GroupAdd,
    IReadOnlyList<UlimitDto> Ulimits,
    bool Init,
    bool AutoRemove);

internal sealed record UlimitDto(string Name, long Soft, long Hard);

internal sealed record CreateContainerResponse(string Id);

internal sealed record ContainerSummaryDto(string Id, IReadOnlyList<string>? Names, string? State, IReadOnlyDictionary<string, string>? Labels);

internal sealed record ContainerInspectDto(string Id, ContainerStateDto State);

internal sealed record ContainerStateDto(string Status, int ExitCode, string? StartedAt, string? FinishedAt);

internal sealed record DockerErrorResponse(string? Message);

internal sealed record PullProgress(string? Status, string? Error);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CreateContainerRequest))]
[JsonSerializable(typeof(CreateContainerResponse))]
[JsonSerializable(typeof(IReadOnlyList<ContainerSummaryDto>))]
[JsonSerializable(typeof(ContainerInspectDto))]
[JsonSerializable(typeof(DockerErrorResponse))]
[JsonSerializable(typeof(PullProgress))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal sealed partial class DockerJsonContext : JsonSerializerContext;
