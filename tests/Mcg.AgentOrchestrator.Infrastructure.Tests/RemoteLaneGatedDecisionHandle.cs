using System.Collections.Concurrent;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.Infrastructure;

// Gate each heartbeat/clock pair until the test permits it or fixture teardown cancels Stop.
internal sealed class RemoteLaneGatedDecisionHandle(SshRemoteLaneHandle inner, CancellationToken stop)
    : IRemoteLaneHandle, IRemoteLaneQueuedJobCancellation, IRemoteLaneAttemptDiagnosticsSource
{
    internal readonly Channel<bool> Permit = Channel.CreateUnbounded<bool>();
    internal readonly Channel<bool> Decisions = Channel.CreateUnbounded<bool>();
    internal readonly Channel<bool> Reads = Channel.CreateUnbounded<bool>();
    internal readonly ConcurrentQueue<DateTimeOffset?> Heartbeats = new();

    public DateTimeOffset? NewestHeartbeat
    {
        get
        {
            var permit = Permit.Reader.ReadAsync(stop).AsTask();
            Reads.Writer.TryWrite(true);
            permit.GetAwaiter().GetResult();
            var heartbeat = inner.NewestHeartbeat;
            Heartbeats.Enqueue(heartbeat);
            return heartbeat;
        }
    }

    public RemoteLaneResult? TryGetResult()
    {
        var result = inner.TryGetResult();
        Decisions.Writer.TryWrite(true);
        return result;
    }

    public void Abandon() => inner.Abandon();
    public void RequestQueuedJobCancellation() => inner.RequestQueuedJobCancellation();
    public RemoteLaneHandleDiagnostics Snapshot() => inner.Snapshot();
    public Task<RemoteLaneRunnerLogCapture> CaptureRunnerLogAsync(CancellationToken cancellationToken) =>
        inner.CaptureRunnerLogAsync(cancellationToken);
}
