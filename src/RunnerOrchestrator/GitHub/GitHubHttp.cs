using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace RunnerOrchestrator.GitHub;

/// <summary>Request plumbing shared by the App credentials and the runner API.</summary>
internal static class GitHubHttp
{
    public const string ApiVersion = "2022-11-28";

    public static HttpClient CreateClient(Uri baseAddress, HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("gh-runners-orchestrator", "0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
        return client;
    }

    public static HttpRequestMessage Request(HttpMethod method, string path, string bearer)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
        {
            return;
        }

        string? message = null;
        try
        {
            var error = await response.Content.ReadFromJsonOrDefaultAsync(GitHubJsonContext.Default.ErrorResponse, cancellationToken).ConfigureAwait(false);
            message = error?.Message;
        }
        catch (JsonException)
        {
            // Not JSON (a proxy page, an empty body): the status code is enough.
        }

        var request = response.RequestMessage;
        throw new GitHubApiException(response.StatusCode, request?.Method.Method ?? "?", request?.RequestUri?.AbsolutePath ?? "?", message);
    }

    private static async Task<T?> ReadFromJsonOrDefaultAsync<T>(this HttpContent content, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return stream.CanSeek && stream.Length == 0
                ? default
                : await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
        }
    }
}
