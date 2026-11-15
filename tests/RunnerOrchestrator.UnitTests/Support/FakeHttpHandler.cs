using System.Net;
using System.Text;

namespace RunnerOrchestrator.UnitTests.Support;

/// <summary>Answers requests from a script and records what was sent, bodies included.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpHandler On(HttpMethod method, string pathAndQueryPrefix, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((r => r.Method == method && r.RequestUri!.PathAndQuery.StartsWith(pathAndQueryPrefix, StringComparison.Ordinal), respond));
        return this;
    }

    public FakeHttpHandler On(HttpMethod method, string pathAndQueryPrefix, HttpStatusCode status, string json = "", IDictionary<string, string>? headers = null) =>
        On(method, pathAndQueryPrefix, _ => Json(status, json, headers));

    public static HttpResponseMessage Json(HttpStatusCode status, string json, IDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var header in headers ?? new Dictionary<string, string>())
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Headers.IfNoneMatch.ToString(), body));
        foreach (var (match, respond) in _routes)
        {
            if (match(request))
            {
                var response = respond(request);
                response.RequestMessage = request;
                return response;
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented) { RequestMessage = request, Content = new StringContent("{\"message\":\"no route in test\"}") };
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string IfNoneMatch, string? Body);
