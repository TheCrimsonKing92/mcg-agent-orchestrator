using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

internal sealed class ManualConductorTimeProviderForTests : TimeProvider
{
    private DateTimeOffset _utcNow;

    internal ManualConductorTimeProviderForTests(DateTimeOffset initialUtcNow)
    {
        _utcNow = initialUtcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    internal void AdvanceForTests(TimeSpan amount)
    {
        if (amount <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Manual conductor time must advance forward.");
        }

        _utcNow = _utcNow.Add(amount);
    }
}

internal sealed class HoldingAcceptanceAttemptsAcrossTicksFixture : IAsyncDisposable
{
    private readonly ManualConductorTimeProviderForTests _timeProvider;
    private readonly ConductorParallelAcceptanceAttemptCompletionGateForTests _completionGate = new();
    private readonly string _attemptRoot;
    private readonly string _stopFilePath;
    private bool _disposed;
    private int _processSpawnCount;

    internal HoldingAcceptanceAttemptsAcrossTicksFixture(
        ManualConductorTimeProviderForTests timeProvider,
        AcceptanceVerificationSummary? completionSummary = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _attemptRoot = Path.Combine(
            Path.GetTempPath(),
            "mcg-conductor-cross-tick",
            Guid.NewGuid().ToString("N"));
        _stopFilePath = Path.Combine(_attemptRoot, "stop.txt");
        Directory.CreateDirectory(_attemptRoot);
        ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();

        AttemptCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            _attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            utcNow: _timeProvider.GetUtcNow,
            isProcessAlive: pid => pid == Environment.ProcessId,
            launchOwnedProcess: _ =>
            {
                Interlocked.Increment(ref _processSpawnCount);
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(Environment.ProcessId);
            },
            tryRunPreSlot: (candidate, _) => ConductorParallelAcceptanceRunResult.Accepted(
                candidate,
                completionSummary ?? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria),
            recentHeartbeatGrace: TimeSpan.FromDays(1),
            attemptCompletionGateForTests: _completionGate);
    }

    internal ConductorParallelAcceptanceAttemptCoordinator AttemptCoordinator { get; }

    internal int HeldAttemptCapacity => ConductorParallelAcceptanceAttemptCompletionGateForTests.Capacity;

    internal int HeldAttemptCount => _completionGate.HeldCount;

    internal int ProcessSpawnCount => Volatile.Read(ref _processSpawnCount);

    internal ConductorParallelAcceptanceAttemptCompletionGateForTests.Handle RequiredHandleForTests(Goal goal) =>
        _completionGate.RequiredHandleForTests(goal.Id.Value);

    internal void AdvanceTimeForTests(TimeSpan amount) => _timeProvider.AdvanceForTests(amount);

    internal BatchLoopSummary RunTickForTests(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Action<BatchTickSummary>? afterTick = null) =>
        CreateLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            _stopFilePath,
            maxIterations: 1,
            onTick: afterTick);

    internal BatchLoopSummary StopLoopForTests(AgentOrchestratorKernel kernel, ConductorDriver driver)
    {
        File.WriteAllText(_stopFilePath, "stop");
        return CreateLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            _stopFilePath,
            maxIterations: 1);
    }

    internal BatchLoopSummary RestartLoopWithTickForTests(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Action<BatchTickSummary>? afterTick = null)
    {
        File.Delete(_stopFilePath);
        return RunTickForTests(kernel, driver, afterTick);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _completionGate.CompleteAllForTests();
        }
        finally
        {
            ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
            if (Directory.Exists(_attemptRoot))
            {
                Directory.Delete(_attemptRoot, recursive: true);
            }

            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private ConductorBatchLoop CreateLoop() => new(utcNow: _timeProvider.GetUtcNow);
}
