using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class FakeRemoteLaneExecutor : IRemoteLaneExecutor
{
    internal ConcurrentQueue<RemoteLaneRequest> Requests { get; } = new();
    internal TaskCompletionSource<RemoteLaneRequest> Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<RemoteLaneRequest, CancellationToken, Task<RemoteLaneSubmission>> Submit { get; set; } =
        (_, _) => Task.FromResult(new RemoteLaneSubmission(null, "fixture-unreachable"));

    public Task<RemoteLaneSubmission> SubmitAsync(RemoteLaneRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        Submitted.TrySetResult(request);
        return Submit(request, cancellationToken);
    }

    internal sealed class Handle(DateTimeOffset heartbeat) : IRemoteLaneHandle
    {
        private RemoteLaneResult? _result;
        private long _heartbeatTicks = heartbeat.UtcTicks;
        private long _lastReadHeartbeatTicks;
        private int _abandoned;
        internal TaskCompletionSource Polled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;
        internal Action? OnPolled { get; set; }
        internal Action? OnHeartbeatRead { get; set; }
        internal DateTimeOffset LastReadHeartbeat => new(Interlocked.Read(ref _lastReadHeartbeatTicks), TimeSpan.Zero);
        public DateTimeOffset? NewestHeartbeat
        {
            get
            {
                var ticks = Interlocked.Read(ref _heartbeatTicks);
                Interlocked.Exchange(ref _lastReadHeartbeatTicks, ticks);
                OnHeartbeatRead?.Invoke();
                return new(ticks, TimeSpan.Zero);
            }
        }
        internal void Heartbeat(DateTimeOffset time) => Interlocked.Exchange(ref _heartbeatTicks, time.UtcTicks);
        internal void Publish(RemoteLaneResult result) => Volatile.Write(ref _result, result);
        public RemoteLaneResult? TryGetResult()
        {
            Polled.TrySetResult();
            OnPolled?.Invoke();
            return Volatile.Read(ref _result);
        }
        public void Abandon()
        {
            Interlocked.Exchange(ref _abandoned, 1);
            Abandoned.TrySetResult();
        }
    }
}
