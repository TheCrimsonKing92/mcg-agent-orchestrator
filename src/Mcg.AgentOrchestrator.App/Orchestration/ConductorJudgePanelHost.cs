using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelHost
{
    private readonly ConductorJudgePanelCaseStore _store;
    private readonly IReadOnlyList<IConductorPanelJudgeRunner> _judges;
    private readonly Func<Goal, string?> _candidate;
    private readonly ConductEventLogWriter _conduct;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly CancellationTokenSource _shutdown = new();
    private PanelCase? _running;
    private Task? _round;
    internal Task? CurrentCase => _round;
    internal bool Enabled { get; set; }

    // Only read-side goal data is used; no state, timeline, hold or intent writer is a dependency.
    internal ConductorJudgePanelHost(ConductorJudgePanelCaseStore store,
        IConductorPanelJudgeRunner sol, IConductorPanelJudgeRunner sonnet,
        Func<Goal, string?> candidate, ConductEventLogWriter conduct,
        Func<DateTimeOffset>? utcNow = null, bool enabled = true)
    {
        if (sol.Judge != "sol" || sonnet.Judge != "sonnet")
            throw new ArgumentException("Panel requires distinct sol and sonnet judges.");
        _store = store;
        _judges = [sol, sonnet];
        _candidate = candidate;
        _conduct = conduct;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        Enabled = enabled;
    }

    internal void ServiceTick(AgentOrchestratorKernel kernel, string? onlyGoalId = null)
    {
        if (!Enabled || _shutdown.IsCancellationRequested) return;
        var now = _utcNow();
        _store.Start(now, kernel.Goals.Select(goal => new PanelGoalEnrollment(goal.Id.Value,
            goal.MetadataCreatedAt ?? goal.BriefVersions[0].RecordedAt)));
        Triggers?.Detect(onlyGoalId);
        _store.ExpireStale(now - ConductorJudgePanelBudgets.ClaimExpiry, _running?.Id);
        Harvest(kernel);
        ReportTerminals();
        if (_round is not null) return;
        if (_judges.All(judge => judge.BindingError is not null))
        {
            _store.SetIdleReason(string.Join(";", _judges.Select(judge => judge.BindingError)));
            return;
        }
        _store.SetIdleReason("");
        var eligible = onlyGoalId is null ? null : new HashSet<string>(StringComparer.Ordinal) { onlyGoalId };
        var claim = _store.ClaimNext(now, eligible);
        if (claim is null) return;
        _running = claim;
        var launches = new List<IConductorPanelJudgeRunner>();
        foreach (var judge in _judges)
        {
            var reason = _store.Health(judge.Judge).State == PanelJudgeHealthState.Suspended ? "judge-suspended" : judge.BindingError;
            var skip = reason is null ? null : new PanelJudgeResult(judge.Judge,
                PanelJudgeOutcome.Skipped, null, "", "", Reason: reason);
            if (_store.ReserveCall(claim, judge.Judge, skip)) launches.Add(judge);
        }
        // Reservation happens before launch. Reading a result happens only after IsCompleted in Harvest.
        _round = Task.Run(() => Task.WhenAll(launches.Select(judge => RunJudge(claim, judge))));
    }

    private async Task RunJudge(PanelCase claim, IConductorPanelJudgeRunner judge)
    {
        PanelJudgeResult result;
        try { result = await judge.RunAsync(claim, _shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { result = new(judge.Judge, PanelJudgeOutcome.TimedOut, null, "", "", Reason: "conductor-stop"); }
        catch (Exception exception)
        { result = new(judge.Judge, PanelJudgeOutcome.InvocationFailed, null, "", "", Reason: exception.Message); }
        _store.RecordResult(claim, result with { Judge = judge.Judge });
    }

    internal void Stop()
    {
        _shutdown.Cancel();
        // Shutdown may drain; the service tick never waits on the judge.
        try { _round?.Wait(ConductorJudgePanelBudgets.ShutdownDrain); }
        catch (AggregateException) { }
        if (_running is { } claim) _store.Preserve(claim, "conductor-stop");
        _running = null;
        _round = null;
    }
}
