using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundGateStartHarness
{
    private readonly Queue<Action> _pending = new();

    internal int StartCount { get; private set; }
    internal int PendingCount => _pending.Count;

    internal void Capture(ConductorDriver driver) => driver.SetCohortGateBackgroundStartForTests(action =>
    {
        StartCount++;
        _pending.Enqueue(action);
    });

    internal void Inline(ConductorDriver driver) => driver.SetCohortGateBackgroundStartForTests(action =>
    {
        StartCount++;
        action();
    });

    internal void RunPending()
    {
        Assert.NotEmpty(_pending);
        _pending.Dequeue()();
    }

    internal sealed class ScriptedAcceptanceVerifier(
        AcceptanceVerificationResult result,
        Action<int, CancellationToken>? onRun = null) : IGoalAcceptanceVerifier
    {
        internal int RunCount { get; private set; }

        public Task<AcceptanceVerificationResult> RunOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            IReadOnlyList<string>? changedFiles,
            int? stableSlotIndex,
            DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            RunCount++;
            onRun?.Invoke(RunCount, ((IAcceptanceRunExecutionContext)executionOwner).CancellationToken);
            return Task.FromResult(result);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            IAcceptanceFocusedVerificationOwner executionOwner,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false) =>
            throw new NotSupportedException("Focused evidence is not used by this gate fixture.");
    }
}
