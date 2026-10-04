namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum PanelTriggerKind
{
    TesterInconclusiveUnchanged, PreReviewEvidence, PreTesterRedLoop,
    ApparatusRed, AuthorAskOwner, CohortBothFailed
}

// A persisted dispute, not an instruction or an adjudication. Unknown revisions stay unknown.
internal sealed record PanelTrigger(PanelTriggerKind Kind, string GoalId, string CandidateSha,
    string BaseSha, string TriggerId, DateTimeOffset RecordedAt, string Text,
    IReadOnlyList<string> ReceiptPaths, string Detail = "")
{
    internal const string UnrecordedSha = "unrecorded";
    internal string KindToken => Kind switch
    {
        PanelTriggerKind.TesterInconclusiveUnchanged => "tester-inconclusive-unchanged",
        PanelTriggerKind.PreReviewEvidence => "pre-review-evidence",
        PanelTriggerKind.PreTesterRedLoop => "pre-tester-red-loop",
        PanelTriggerKind.ApparatusRed => "apparatus-red",
        PanelTriggerKind.AuthorAskOwner => "author-ask-owner",
        PanelTriggerKind.CohortBothFailed => "cohort-both-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };
}

internal sealed class ConductorJudgePanelTriggerDetector(
    ConductorJudgePanelCaseStore store, ConductorJudgePanelTriggerSources sources,
    ConductorJudgePanelPacketBuilder packets)
{
    internal IReadOnlyList<PanelCase> Detect(string? onlyGoalId = null)
    {
        var existing = store.Cases().ToList();
        var added = new List<PanelCase>();
        var snapshots = new Dictionary<string, Mcg.AgentOrchestrator.Core.GoalSnapshot?>();
        foreach (var persisted in sources.Read()
                     .OrderBy(item => item.RecordedAt).ThenBy(item => item.TriggerId, StringComparer.Ordinal))
        {
            try
            {
                var trigger = persisted;
                if (!snapshots.TryGetValue(trigger.GoalId, out var snapshot))
                    snapshots[trigger.GoalId] = snapshot = sources.ReadGoal(trigger.GoalId);
                if (snapshot is not null) trigger = trigger with { GoalId = snapshot.Id };
                if (onlyGoalId is not null && trigger.GoalId != onlyGoalId) continue;
                // An already captured event owns its decision-time criteria version. A later
                // update to the goal snapshot cannot reinterpret that same persisted event.
                if (existing.Any(item => item.Key.GoalId == trigger.GoalId &&
                        item.Key.CandidateSha == trigger.CandidateSha && item.Key.BaseSha == trigger.BaseSha &&
                        item.Key.TriggerId == trigger.TriggerId)) continue;
                var criteria = ConductorJudgePanelCriteriaAtTrigger.Resolve(snapshot, trigger.RecordedAt);
                var key = new PanelCaseKey(trigger.GoalId, trigger.CandidateSha, trigger.BaseSha,
                    criteria.Version, trigger.TriggerId, trigger.KindToken, "");
                // Re-raised timeline/conduct escalations retain the earliest persisted case, even
                // when the producer writes a new cursor. Author items and cohort receipts are distinct disputes.
                if (existing.Any(item => item.Id == ConductorJudgePanelCaseStore.CaseId(key) ||
                        (trigger.Kind is not (PanelTriggerKind.AuthorAskOwner or PanelTriggerKind.CohortBothFailed) &&
                         item.Key.GoalId == key.GoalId && item.Key.CandidateSha == key.CandidateSha &&
                         item.Key.BaseSha == key.BaseSha && item.Key.CriteriaVersion == key.CriteriaVersion &&
                         item.Key.TriggerKind == key.TriggerKind))) continue;
                var packet = packets.Build(trigger, criteria);
                var enrolled = store.Enqueue(key with { Packet = packet.Text });
                existing.Add(enrolled);
                added.Add(enrolled);
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or ArgumentException)
            {
                // A bad persisted dispute cannot starve later disputes or host maintenance.
                // Retain its identity and diagnostic in the panel ledger, never in a producer store.
                store.RecordTriggerFailure(persisted.TriggerId, persisted.GoalId, exception);
            }
        }
        return added;
    }
}
