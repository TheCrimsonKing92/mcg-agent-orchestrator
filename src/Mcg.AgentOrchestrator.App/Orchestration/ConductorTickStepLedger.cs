using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One tick owns this telemetry. Ambient scope reaches synchronous CLI/store seams,
// but is restored on every exit and cannot keep recording after the tick ends.
internal sealed class ConductorTickStepLedger
{
    internal static readonly IReadOnlyList<string> PrewalkNames = Array.AsReadOnly(new[]
    {
        "retire-until-goal-lessons", "workspace-intents", "steward", "author", "store-evidence",
        "judge-panel", "board-fill", "operator-intent-list", "eligibility"
    });
    internal static readonly IReadOnlyList<string> SweepNames = Array.AsReadOnly(new[]
    {
        "kernel-reload", "refresh-dispatches", "parked-reloads", "terminal-sweep-run", "owned-root-reap",
        "reconcile-remediation", "host-health", "maintenance", "state-log-check", "failure-clusters",
        "remote-git-mirror", "goal-refinement"
    });
    private static readonly AsyncLocal<ConductorTickStepLedger?> Current = new();
    private readonly Func<TimeSpan> _cpu;
    private readonly Action<string>? _probe;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Ticks, long Calls)> _steps = new(StringComparer.Ordinal);
    private readonly Stack<Frame> _frames = new();
    private bool _active;
    private long _goalsHydrated, _metadataRows, _terminalJournalStats, _backlogRows, _readinessEvaluations;

    internal ConductorTickStepLedger(Func<TimeSpan> cpu, Action<string>? probe = null)
    {
        _cpu = cpu;
        _probe = probe;
    }

    internal IDisposable Activate()
    {
        var previous = Current.Value;
        lock (_gate) _active = true;
        Current.Value = this;
        return new Lifetime(this, previous);
    }

    internal static T Measure<T>(string name, Func<T> action)
    {
        var ledger = Current.Value;
        if (ledger is null) return action();
        Frame? frame = null;
        bool active;
        lock (ledger._gate)
        {
            active = ledger._active;
            if (active)
            {
                var old = ledger._steps.GetValueOrDefault(name);
                ledger._steps[name] = (old.Ticks, old.Calls + 1);
                // Other threads still count calls; their process CPU overlaps the owner's
                // scopes and must not be added to this partition of the phase.
                frame = Environment.CurrentManagedThreadId == ledger._ownerThread
                    ? new Frame(ledger._cpu()) : null;
                if (frame is not null) ledger._frames.Push(frame);
            }
        }
        if (!active) return action();
        try
        {
            var result = action();
            ledger._probe?.Invoke(name);
            return result;
        }
        finally
        {
            if (frame is not null)
            {
                lock (ledger._gate)
                {
                    var elapsed = Math.Max(0, (ledger._cpu() - frame.Start).Ticks);
                    ledger._frames.Pop();
                    if (ledger._frames.TryPeek(out var parent)) parent.ChildTicks += elapsed;
                    var old = ledger._steps[name];
                    ledger._steps[name] = (old.Ticks + elapsed - frame.ChildTicks, old.Calls);
                }
            }
        }
    }

    internal static void Measure(string name, Action action) => Measure(name, () => { action(); return true; });

    internal static IReadOnlyList<T> CountMetadataRows<T>(IReadOnlyList<T> rows)
    {
        AddCounter(ledger => Interlocked.Add(ref ledger._metadataRows, rows.Count));
        return rows;
    }

    internal static AgentOrchestratorKernel CountGoalsHydrated(AgentOrchestratorKernel kernel)
    {
        AddCounter(ledger => Interlocked.Add(ref ledger._goalsHydrated, kernel.Goals.Count));
        return kernel;
    }

    internal static T CountTerminalJournalCheck<T>(Func<T> read)
    {
        AddCounter(ledger => Interlocked.Increment(ref ledger._terminalJournalStats));
        return read();
    }

    internal static IReadOnlyList<T> CountBacklogRows<T>(IReadOnlyList<T> rows)
    {
        AddCounter(ledger => Interlocked.Add(ref ledger._backlogRows, rows.Count));
        return rows;
    }

    internal static Func<BacklogItem, BacklogReadiness> CountReadinessEvaluations(Func<BacklogItem, BacklogReadiness> evaluate)
    {
        // Capture this tick, rather than resolving a later tick at evaluation time.
        var ledger = Current.Value;
        if (ledger is null) return evaluate;
        return item =>
        {
            lock (ledger._gate)
                if (ledger._active) Interlocked.Increment(ref ledger._readinessEvaluations);
            return evaluate(item);
        };
    }

    private static void AddCounter(Action<ConductorTickStepLedger> increment)
    {
        var ledger = Current.Value;
        if (ledger is null) return;
        lock (ledger._gate)
            if (ledger._active) increment(ledger);
    }

    internal string FormatSteps(IReadOnlyList<string> names)
    {
        lock (_gate)
            return string.Join(',', names.Select(name =>
            {
                var value = _steps.GetValueOrDefault(name);
                return FormattableString.Invariant($"{name}:{value.Ticks / TimeSpan.TicksPerMillisecond}:{value.Calls}");
            }));
    }

    internal string FormatCounters() => string.Join(',', new[]
    {
        ("goals_hydrated", Interlocked.Read(ref _goalsHydrated)),
        ("metadata_rows", Interlocked.Read(ref _metadataRows)),
        ("terminal_journal_stats", Interlocked.Read(ref _terminalJournalStats)),
        ("backlog_rows_read", Interlocked.Read(ref _backlogRows)),
        ("readiness_evaluations", Interlocked.Read(ref _readinessEvaluations))
    }.Select(pair => pair.Item1 + "=" + pair.Item2.ToString(CultureInfo.InvariantCulture)));

    private sealed class Frame(TimeSpan start)
    {
        internal TimeSpan Start { get; } = start;
        internal long ChildTicks { get; set; }
    }

    private sealed class Lifetime(ConductorTickStepLedger ledger, ConductorTickStepLedger? previous) : IDisposable
    {
        public void Dispose()
        {
            lock (ledger._gate) ledger._active = false;
            Current.Value = previous;
        }
    }
}
