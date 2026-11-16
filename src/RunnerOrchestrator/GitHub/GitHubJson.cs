using System.Text.Json.Serialization;

namespace RunnerOrchestrator.GitHub;

// Wire shapes of the few GitHub REST endpoints the orchestrator calls. Only the fields it
// reads are declared. Classes, not records, for the ones that carry a secret, so a
// compiler-generated ToString never prints it.

internal sealed record WorkflowRunsResponse(IReadOnlyList<WorkflowRunDto> WorkflowRuns);

internal sealed record WorkflowRunDto(long Id, string? Status);

internal sealed record JobsResponse(IReadOnlyList<JobDto> Jobs);

internal sealed record JobDto(long Id, long RunId, string? Status, IReadOnlyList<string>? Labels, string? RunnerName, DateTimeOffset? CreatedAt);

internal sealed record JitConfigRequest(string Name, long RunnerGroupId, IReadOnlyList<string> Labels, string WorkFolder);

internal sealed class JitConfigResponse
{
    public RunnerDto? Runner { get; init; }

    public string? EncodedJitConfig { get; init; }
}

internal sealed record RunnersResponse(IReadOnlyList<RunnerDto> Runners);

internal sealed record RunnerDto(long Id, string Name, string? Status, bool Busy);

internal sealed record RepositoryDto(string FullName, bool Private, OwnerDto Owner);

internal sealed record OwnerDto(string Login);

internal sealed record InstallationDto(long Id, long AppId, OwnerDto Account);

internal sealed record AccessTokenRequest(IReadOnlyList<string>? Repositories, AccessTokenPermissions Permissions);

internal sealed record AccessTokenPermissions(string? Actions, string? Administration, string Metadata);

internal sealed record InstallationRepositoriesResponse(IReadOnlyList<RepositoryDto> Repositories);

internal sealed class AccessTokenResponse
{
    public string? Token { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed record ErrorResponse(string? Message);

internal sealed record ReleaseDto(string TagName);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InstallationRepositoriesResponse))]
[JsonSerializable(typeof(WorkflowRunsResponse))]
[JsonSerializable(typeof(JobsResponse))]
[JsonSerializable(typeof(JitConfigRequest))]
[JsonSerializable(typeof(JitConfigResponse))]
[JsonSerializable(typeof(RunnersResponse))]
[JsonSerializable(typeof(RepositoryDto))]
[JsonSerializable(typeof(InstallationDto))]
[JsonSerializable(typeof(AccessTokenRequest))]
[JsonSerializable(typeof(AccessTokenResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ReleaseDto))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
