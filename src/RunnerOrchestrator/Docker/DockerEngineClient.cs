using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace RunnerOrchestrator.Docker;

/// <summary>
/// A small Docker Engine API client over the Unix socket (ADR 0003). Error messages carry the
/// Engine's message and the request path only; the container environment is never echoed.
/// </summary>
internal sealed class DockerEngineClient(HttpClient http) : IContainerRuntime, IDisposable
{
    /// <summary>Docker Engine 25 and later; Engine 29 accepts 1.44 and newer.</summary>
    public const string ApiVersion = "v1.47";

    public static DockerEngineClient ForSocket(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new DockerEngineClient(CreateHttpClient(handler));
    }

    public static HttpClient CreateHttpClient(HttpMessageHandler handler) =>
        // The connection goes to the local Unix socket (ConnectCallback); the host name is never
        // resolved and nothing leaves the machine, so there is no TLS to use.
        new(handler) { BaseAddress = new Uri($"http://docker/{ApiVersion}/"), Timeout = TimeSpan.FromMinutes(10) }; // NOSONAR S5332

    public async Task EnsureImageAsync(string image, CancellationToken cancellationToken)
    {
        using (var inspect = await http.GetAsync($"images/{image}/json", cancellationToken).ConfigureAwait(false))
        {
            if (inspect.IsSuccessStatusCode)
            {
                return;
            }

            if (inspect.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(inspect, cancellationToken).ConfigureAwait(false);
            }
        }

        using var pull = await http.PostAsync($"images/create?fromImage={Uri.EscapeDataString(image)}", content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(pull, cancellationToken).ConfigureAwait(false);
        await ReadPullStreamAsync(pull, image, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CreateAsync(ContainerSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var body = new CreateContainerRequest(
            spec.Image,
            [.. spec.Environment.Select(e => $"{e.Key}={e.Value}")],
            spec.Labels,
            new HostConfigDto(
                CpusetCpus: spec.Cpuset,
                Memory: spec.MemoryBytes,
                MemorySwap: spec.MemoryBytes,
                NetworkMode: "host",
                Binds: [$"{spec.DockerSocket}:{spec.DockerSocket}"],
                GroupAdd: [spec.DockerSocketGroupId.ToString(CultureInfo.InvariantCulture)],
                Ulimits: [new UlimitDto("nofile", spec.NofileLimit, spec.NofileLimit)],
                Init: true,
                AutoRemove: false));
        using var response = await http.PostAsJsonAsync($"containers/create?name={Uri.EscapeDataString(spec.Name)}", body, DockerJsonContext.Default.CreateContainerRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync(DockerJsonContext.Default.CreateContainerResponse, cancellationToken).ConfigureAwait(false);
        return created?.Id ?? throw new DockerEngineException("Docker returned no container id.");
    }

    public async Task StartAsync(string containerId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync($"containers/{Uri.EscapeDataString(containerId)}/start", content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContainerSummary>> ListByLabelAsync(string label, CancellationToken cancellationToken)
    {
        var filters = JsonSerializer.Serialize(new Dictionary<string, string[]> { ["label"] = [label] }, DockerJsonContext.Default.DictionaryStringStringArray);
        using var response = await http.GetAsync($"containers/json?all=true&filters={Uri.EscapeDataString(filters)}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var containers = await response.Content.ReadFromJsonAsync(DockerJsonContext.Default.IReadOnlyListContainerSummaryDto, cancellationToken).ConfigureAwait(false) ?? [];
        return [.. containers.Select(c => new ContainerSummary(
            c.Id,
            c.Names is { Count: > 0 } names ? names[0].TrimStart('/') : string.Empty,
            c.State ?? string.Empty,
            c.Labels ?? new Dictionary<string, string>()))];
    }

    public async Task<ContainerState> InspectAsync(string containerId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"containers/{Uri.EscapeDataString(containerId)}/json", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var inspect = await response.Content.ReadFromJsonAsync(DockerJsonContext.Default.ContainerInspectDto, cancellationToken).ConfigureAwait(false)
            ?? throw new DockerEngineException("Docker returned an empty inspect result.");
        return new ContainerState(inspect.State.Status, inspect.State.ExitCode, ParseTime(inspect.State.StartedAt), ParseTime(inspect.State.FinishedAt));
    }

    public async Task RemoveAsync(string containerId, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"containers/{Uri.EscapeDataString(containerId)}?v=true&force=true", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => http.Dispose();

    /// <summary>Docker reports "never" as year 1; that and empty become null.</summary>
    internal static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) && parsed.Year > 1
            ? parsed
            : null;

    private static async Task ReadPullStreamAsync(HttpResponseMessage response, string image, CancellationToken cancellationToken)
    {
        // The pull answers 200 at once and reports a failure inside the progress stream.
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var progress = JsonSerializer.Deserialize(line, DockerJsonContext.Default.PullProgress);
            if (!string.IsNullOrEmpty(progress?.Error))
            {
                throw new DockerEngineException($"Pulling {image} failed: {progress.Error}");
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
        {
            return;
        }

        string? message = null;
        try
        {
            message = (await response.Content.ReadFromJsonAsync(DockerJsonContext.Default.DockerErrorResponse, cancellationToken).ConfigureAwait(false))?.Message;
        }
        catch (JsonException)
        {
            // Not JSON: the status code is enough.
        }

        var request = response.RequestMessage;
        throw new DockerEngineException($"Docker answered {(int)response.StatusCode} to {request?.Method.Method} {request?.RequestUri?.AbsolutePath}: {message ?? "(no message)"}");
    }
}
