using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TerminalGoalSweepAttentionAwaitingGateTests : IDisposable
{
    // Each fact owns its kernel and a unique database directory, so this class is parallel-safe.
    private readonly string _databaseRoot = Path.Combine(
        Path.GetTempPath(), "mcg-sweep-attention-awaiting-gate-tests", Guid.NewGuid().ToString("N"));
    private readonly AgentOrchestratorKernel _kernel = new();
    private readonly Goal _goal;
    private readonly CollaborationItemStore _store;

    public TerminalGoalSweepAttentionAwaitingGateTests()
    {
        _goal = _kernel.CreateGoal("Completed branch needs acceptance");
        _store = CollaborationItemStore.ForDirectory(_databaseRoot);
    }

    [Xunit.Fact]
    public async Task AwaitingGateBlockerRaisesNoItem()
    {
        var blocker = Blocker(TerminalGoalRemedy.Acceptance(_goal.Id, Prefix, null));

        var changes = await TerminalGoalSweepAttention.SurfaceAsync(_kernel, Sweep(blocker), _store);

        Xunit.Assert.Equal(0, changes);
        Xunit.Assert.Empty(await _store.ListAsync(_goal.Id.Value));
    }

    [Xunit.Fact]
    public async Task AwaitingGateBlockerResolvesEarlierOperatorOnlyItem()
    {
        var operatorBlocker = Blocker(TerminalGoalRemedy.OperatorOnly(
            _goal.Id, Prefix, $"attention show {Prefix}"));
        var raised = await TerminalGoalSweepAttention.SurfaceAsync(_kernel, Sweep(operatorBlocker), _store);
        var earlierItem = Xunit.Assert.Single(await _store.ListAsync(_goal.Id.Value));
        Xunit.Assert.Equal(1, raised);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, earlierItem.Status);
        var awaitingGateBlocker = Blocker(TerminalGoalRemedy.Acceptance(_goal.Id, Prefix, null));

        var changes = await TerminalGoalSweepAttention.SurfaceAsync(_kernel, Sweep(awaitingGateBlocker), _store);

        Xunit.Assert.Equal(1, changes);
        var resolved = Xunit.Assert.Single(await _store.ListAsync(_goal.Id.Value));
        Xunit.Assert.Equal(earlierItem.Id, resolved.Id);
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Xunit.Assert.Equal("terminal sweep blocker resolved", resolved.Resolution);
    }

    [Xunit.Fact]
    public async Task GatePassedArtifactBlockerStillRaisesDecision()
    {
        var blocker = Blocker(TerminalGoalRemedy.Acceptance(
            _goal.Id, Prefix, new TerminalGoalGateArtifact("gate-1", "candidate-sha", "gate-passed")));

        var changes = await TerminalGoalSweepAttention.SurfaceAsync(_kernel, Sweep(blocker), _store);

        Xunit.Assert.Equal(1, changes);
        var item = Xunit.Assert.Single(await _store.ListAsync(_goal.Id.Value));
        Xunit.Assert.Equal(CollaborationItemType.Decision, item.Type);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, item.Status);
    }

    [Xunit.Fact]
    public async Task OperatorOnlyBlockerStillRaisesDecision()
    {
        var blocker = Blocker(TerminalGoalRemedy.OperatorOnly(
            _goal.Id, Prefix, $"attention show {Prefix}"));

        var changes = await TerminalGoalSweepAttention.SurfaceAsync(_kernel, Sweep(blocker), _store);

        Xunit.Assert.Equal(1, changes);
        var item = Xunit.Assert.Single(await _store.ListAsync(_goal.Id.Value));
        Xunit.Assert.Equal(CollaborationItemType.Decision, item.Type);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, item.Status);
    }

    [Xunit.Fact]
    public async Task AwaitingGateBlockerDoesNotSuppressOtherBlockerKinds()
    {
        var remedy = TerminalGoalRemedy.Acceptance(_goal.Id, Prefix, null);
        var awaitingGateBlocker = Blocker(remedy);
        var otherBlocker = new TerminalGoalSweepBlocker(
            "verified-merged-branch-missing-integrate-commit", "integrate commit is missing", remedy);

        var changes = await TerminalGoalSweepAttention.SurfaceAsync(
            _kernel, Sweep(awaitingGateBlocker, otherBlocker), _store);

        Xunit.Assert.Equal(1, changes);
        var item = Xunit.Assert.Single(await _store.ListAsync(_goal.Id.Value));
        Xunit.Assert.Equal(CollaborationItemType.Decision, item.Type);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, item.Status);
        Xunit.Assert.Equal($"terminal-sweep-blocker:{_goal.Id.Value}:{otherBlocker.Kind}", item.CorrelationKey);
    }

    private string Prefix => _goal.Id.Value[..8];

    private static TerminalGoalSweepBlocker Blocker(TerminalGoalRemedy remedy) =>
        new("completed-branch-unmerged", "verified goal still has an unmerged branch", remedy);

    private TerminalGoalSweepResult Sweep(params TerminalGoalSweepBlocker[] blockers) =>
        new([
            new TerminalGoalSweepGoalResult(_goal.Id, Prefix, [], blockers)
        ], SweptGoalIds: [_goal.Id]);

    public void Dispose()
    {
        if (Directory.Exists(_databaseRoot))
        {
            Directory.Delete(_databaseRoot, recursive: true);
        }
    }
}
