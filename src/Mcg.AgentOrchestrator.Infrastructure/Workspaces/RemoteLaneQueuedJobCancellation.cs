namespace Mcg.AgentOrchestrator.Infrastructure;

// Cancellation requests cover both queued and started jobs.
internal interface IRemoteLaneQueuedJobCancellation
{
    void RequestQueuedJobCancellation();
}
