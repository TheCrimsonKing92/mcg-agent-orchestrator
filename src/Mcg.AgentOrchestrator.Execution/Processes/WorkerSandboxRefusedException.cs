namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerSandboxRefusedException(string reasonCode, string path, Exception? innerException = null)
    : InvalidOperationException($"WORKER_SANDBOX_REFUSED reason={reasonCode} path={path}", innerException)
{
    public string ReasonCode { get; } = reasonCode;

    public string Path { get; } = path;
}
