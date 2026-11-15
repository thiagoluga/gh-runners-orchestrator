using System.Net;

namespace RunnerOrchestrator.GitHub;

/// <summary>A non-success answer from GitHub. Carries the method, path and GitHub's message, never a header or token.</summary>
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
        : base($"GitHub answered {(int)statusCode} to {method} {path}: {Truncate(gitHubMessage)}")
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }

    private static string Truncate(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "(no message)";
        }

        return message.Length <= 200 ? message : message[..200];
    }
}
