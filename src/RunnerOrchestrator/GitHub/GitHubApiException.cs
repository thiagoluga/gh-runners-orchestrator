using System.Net;

namespace RunnerOrchestrator.GitHub;

/// <summary>A non-success answer from GitHub. Untrusted response bodies are never echoed.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException()
    {
    }

    public GitHubApiException(string message)
        : base(message)
    {
    }

    public GitHubApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GitHubApiException(HttpStatusCode statusCode, string method, string path, string? gitHubMessage)
        : base($"GitHub answered {(int)statusCode} to {method} {path}: {SafeReason(gitHubMessage)}")
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }

    private static string SafeReason(string? message) => string.IsNullOrEmpty(message)
        ? "(no message)" : "(response message omitted)";
}
