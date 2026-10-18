namespace Mcg.AgentOrchestrator.Infrastructure;

// Null means the runner state could not be read; it is not proof that a job has left.
internal interface IRemoteLaneQueueStateSource
{
    Task<bool?> IsJobListedAsync(RemoteLaneRequest request, CancellationToken cancellationToken);
}
