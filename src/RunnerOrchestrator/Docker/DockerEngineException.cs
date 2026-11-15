namespace RunnerOrchestrator.Docker;

/// <summary>A failed Docker Engine call; the message names the path and the Engine's message only.</summary>
public sealed class DockerEngineException : Exception
{
    public DockerEngineException()
    {
    }

    public DockerEngineException(string message)
        : base(message)
    {
    }

    public DockerEngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
