using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PanelSourceReadCounter
{
    internal int EventLines { get; private set; }
    internal int ParsedLines { get; private set; }
    internal int GoalReads { get; private set; }
    internal int StoreScans { get; private set; }
    internal Action? OnEventLine { get; init; }
    internal void EventLine() { EventLines++; OnEventLine?.Invoke(); }
    internal void ParsedLine() => ParsedLines++;
    internal void GoalRead() => GoalReads++;
    internal void StoreScan() => StoreScans++;
    internal void Reset() => EventLines = ParsedLines = GoalReads = StoreScans = 0;
}

internal sealed partial class ConductorJudgePanelTriggerSources
{
    internal PanelSourceReadCounter? Reads { get; init; }

    internal PanelSourceBatch ReadSince(PanelSourceState previous)
    {
        var state = previous.Initialized ? previous.CopyCursors() : PanelSourceState.Empty();
        var copiedEvidence = !previous.Initialized;
        void CopyEvidence()
        {
            if (copiedEvidence) return;
            state = state with { Retained = state.Retained.Copy() };
            copiedEvidence = true;
        }
        var observed = new HashSet<string>(StringComparer.Ordinal) { "scan-complete", "author-store", "cohort-store" };
        var conduct = new List<OwnerConductEvent>();
        var directory = Path.GetDirectoryName(conductPath);
        var files = directory is not null && Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, Path.GetFileNameWithoutExtension(conductPath) + "-*.log")
                .Prepend(conductPath).Distinct(StringComparer.Ordinal) : [];
        // Read conduct first: its writer emits a held line after the timeline decision.
        foreach (var path in files)
        foreach (var line in NewLines(path, state, observed))
            if (ConductEventFileSource.TryParse(line, out var item)) conduct.Add(item!);
        conduct = conduct.OrderBy(item => item.Timestamp).ToList();
        foreach (var item in conduct.Where(item => item.EventKind == "goal-escalation" &&
                     item.Detail.StartsWith("author-owner-question ", StringComparison.Ordinal)))
        {
            CopyEvidence();
            state.Retained.Escalations.TryAdd($"{item.GoalId}:{item.Timestamp:O}:{item.Detail}", item);
        }

        var timeline = new List<PanelTrigger>();
        if (Directory.Exists(eventsDirectory))
            foreach (var path in Directory.EnumerateFiles(eventsDirectory, "*.jsonl").Order(StringComparer.Ordinal))
            foreach (var line in NewLines(path, state, observed))
                if (TimelineTrigger(line) is { } trigger) timeline.Add(trigger);
        foreach (var trigger in timeline.Where(item => item.Kind == PanelTriggerKind.ApparatusRed))
        {
            CopyEvidence();
            state.Retained.Apparatus.TryAdd(trigger.TriggerId, new(trigger));
        }

        var disputes = ReadConductDisputes(conduct).ToArray();
        if (disputes.Any(item => item.Kind == PanelTriggerKind.ApparatusRed)) CopyEvidence();
        var result = timeline.Concat(PairApparatus(state.Retained, disputes)).ToList();
        if (StoreChanged(authorPath, "author-store", state))
        {
            CopyEvidence();
            result.AddRange(ReadAuthor(state.Retained.Escalations.Values.OrderBy(item => item.Timestamp).ToArray(), state.Retained.Author));
        }
        if (StoreChanged(cohortPath, "cohort-store", state))
        {
            CopyEvidence();
            result.AddRange(ReadCohort(state.Retained.Cohort));
        }
        foreach (var missing in state.Cursors.Keys.Except(observed).ToArray()) state.Cursors.Remove(missing);
        state.Cursors["scan-complete"] = new(0, 0, 0, 0, "");
        var changed = !previous.Initialized || previous.Cursors.Count != state.Cursors.Count ||
            state.Cursors.Any(item => !previous.Cursors.TryGetValue(item.Key, out var old) || old != item.Value);
        return new(result, state, changed);
    }

    private static IEnumerable<PanelTrigger> PairApparatus(PanelSourceRetained retained, IEnumerable<PanelTrigger> conduct)
    {
        foreach (var trigger in conduct)
        {
            if (trigger.Kind == PanelTriggerKind.ApparatusRed && ApparatusRegate(trigger.Text) is { } regate)
            {
                if (retained.Apparatus.Values.Any(copy => copy.PairedBy == trigger.TriggerId)) continue;
                var matches = retained.Apparatus.Values.Where(copy => copy.PairedBy is null &&
                        copy.Trigger.RecordedAt <= trigger.RecordedAt && trigger.GoalId.Length >= 8 &&
                        copy.Trigger.GoalId.StartsWith(trigger.GoalId, StringComparison.Ordinal) &&
                        ApparatusRegate(copy.Trigger.Text) is { Ordinal: not null } decision && decision.Kind == regate.Kind &&
                        (regate.Ordinal is null || decision.Ordinal == regate.Ordinal))
                    .OrderByDescending(copy => copy.Trigger.RecordedAt).ThenBy(copy => copy.Trigger.TriggerId, StringComparer.Ordinal).ToArray();
                if (matches.Length > 0 && matches.Select(copy => copy.Trigger.GoalId).Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    retained.Apparatus[matches[0].Trigger.TriggerId] = matches[0] with { PairedBy = trigger.TriggerId };
                    continue;
                }
            }
            yield return trigger;
        }
    }

    private static bool StoreChanged(string path, string source, PanelSourceState state)
    {
        // Claims and receipts can be updated after insert. Probe all SQLite commit files,
        // then consume eligible identities, rather than skipping older unfinished rows.
        var signature = string.Join("|", new[] { path, path + "-wal", path + "-journal" }.Select(file =>
        {
            var info = new FileInfo(file);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}" : "absent";
        }));
        var next = new PanelSourceCursor(0, 0, 0, 0, signature);
        var changed = !state.Cursors.TryGetValue(source, out var old) || old != next;
        state.Cursors[source] = next;
        return changed && File.Exists(path);
    }
}
