using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only the panel ledger owns these checkpoints. Retained producer evidence preserves
// pairing and packet construction when the byte stream is no longer scanned in full.
internal sealed record PanelSourceCursor(long Offset, long Length, long WriteTicks, long CreationTicks, string Fingerprint, string Identity = "");
internal sealed record PanelApparatusCopy(PanelTrigger Trigger, string? PairedBy = null);
internal sealed record PanelDeferredTrigger(PanelTrigger Trigger, string ResolvedGoalId);
internal sealed record PanelSourceRetained(
    HashSet<string> Author, HashSet<string> Cohort, Dictionary<string, PanelApparatusCopy> Apparatus,
    Dictionary<string, OwnerConductEvent> Escalations, Dictionary<string, PanelDeferredTrigger> Deferred)
{
    internal static PanelSourceRetained Empty() => new([], [], new(StringComparer.Ordinal),
        new(StringComparer.Ordinal), new(StringComparer.Ordinal));
    internal PanelSourceRetained Copy() => new(new(Author, StringComparer.Ordinal), new(Cohort, StringComparer.Ordinal),
        new(Apparatus, StringComparer.Ordinal), new(Escalations, StringComparer.Ordinal), new(Deferred, StringComparer.Ordinal));
}
internal sealed record PanelSourceState(Dictionary<string, PanelSourceCursor> Cursors, PanelSourceRetained Retained)
{
    internal bool Initialized => Cursors.ContainsKey("scan-complete");
    internal static PanelSourceState Empty() => new(new(StringComparer.Ordinal), PanelSourceRetained.Empty());
    internal PanelSourceState CopyCursors() => new(new(Cursors, StringComparer.Ordinal), Retained);
}
internal sealed record PanelSourceBatch(IReadOnlyList<PanelTrigger> Triggers, PanelSourceState State, bool Changed);

internal sealed partial class ConductorJudgePanelCaseStore
{
    internal bool HasSourceCursors()
    {
        using var connection = Open();
        return Scalar(connection, null, "SELECT 1 FROM panel_source_cursors WHERE source = 'scan-complete'") is not null;
    }

    internal PanelSourceState LoadSourceState()
    {
        using var connection = Open();
        var state = PanelSourceState.Empty();
        using (var command = Command(connection, null, "SELECT source, cursor_json FROM panel_source_cursors"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) state.Cursors.Add(reader.GetString(0), JsonSerializer.Deserialize<PanelSourceCursor>(reader.GetString(1))!);
        if (!state.Initialized) return PanelSourceState.Empty();
        var json = Scalar(connection, null, "SELECT value FROM panel_source_retained WHERE key = 'evidence'") as string;
        return json is null ? state : state with { Retained = JsonSerializer.Deserialize<PanelSourceRetained>(json)! };
    }

    internal void CommitSourceAdvance(PanelSourceState previous, PanelSourceState next)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!previous.Initialized)
        {
            Execute(connection, transaction, "DELETE FROM panel_source_cursors");
            Execute(connection, transaction, "DELETE FROM panel_source_retained");
        }
        foreach (var source in previous.Cursors.Keys.Except(next.Cursors.Keys))
            Execute(connection, transaction, "DELETE FROM panel_source_cursors WHERE source = $source", ("$source", source));
        foreach (var (source, cursor) in next.Cursors)
            if (!previous.Cursors.TryGetValue(source, out var old) || old != cursor)
                Execute(connection, transaction, "INSERT OR REPLACE INTO panel_source_cursors VALUES ($source, $json)",
                    ("$source", source), ("$json", JsonSerializer.Serialize(cursor)));
        Execute(connection, transaction, "INSERT OR REPLACE INTO panel_source_retained VALUES ('evidence', $json)",
            ("$json", JsonSerializer.Serialize(next.Retained)));
        transaction.Commit();
    }
}
