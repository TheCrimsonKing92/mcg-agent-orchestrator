using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleEventRefreshFilter
{
    private readonly object _sync = new();
    private DateTimeOffset? _skipped;

    // Maps protects activity; committed changes protect board and decisions;
    // gate/evidence kinds and ACCEPTANCE details protect the gate pane; tick protects latest tick.
    internal static bool ChangesView(OwnerConductEvent item) =>
        OwnerActivityNarrator.Maps(item) ||
        ChangeStreamRecord.TryClassify(item.EventKind, item.GoalId, out _) ||
        item.EventKind is "gate-progress" or "remote-lane" or "EVIDENCE_START" or "EVIDENCE_END" ||
        item.Detail.StartsWith("ACCEPTANCE", StringComparison.Ordinal) ||
        item.EventKind.StartsWith("tick", StringComparison.OrdinalIgnoreCase);

    internal void Route(OwnerConductEvent item, OwnerConsoleRefreshCoalescer coalescer)
    {
        if (ChangesView(item))
        {
            coalescer.Request(item, OwnerConsoleRefreshScope.None);
            return;
        }
        lock (_sync)
        {
            if (_skipped is null || item.Timestamp > _skipped) _skipped = item.Timestamp;
        }
    }

    internal DateTimeOffset? FoldLast(DateTimeOffset? last)
    {
        lock (_sync)
        {
            if (_skipped is { } skipped && (last is null || skipped > last)) last = skipped;
            _skipped = null;
            return last;
        }
    }
}
