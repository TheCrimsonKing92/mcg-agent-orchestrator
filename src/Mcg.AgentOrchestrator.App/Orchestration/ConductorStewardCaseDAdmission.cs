using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorStewardCaseDAdmission
{
    internal static bool Admits(Goal goal, TaskSpec task, AdjudicateOperatorIntentPayload payload,
        string? currentHead, IReadOnlyList<AcceptanceFailingTestIndexRecord> records, int cap)
    {
        var cases = (payload.EvidenceReferences ?? [])
            .Where(reference => reference?.StartsWith("steward-case=", StringComparison.Ordinal) == true).ToArray();
        return string.Equals(payload.Shape?.Trim(), "close", StringComparison.OrdinalIgnoreCase) &&
               cases.Length == 1 && cases[0] == "steward-case=D" &&
               payload.Precedent?.StartsWith("steward-case=D trigger=", StringComparison.Ordinal) == true &&
               ConductorStewardTriggerDetector.IsCaseDTask(goal, task, currentHead) &&
               AcceptanceFailingTestIndex.CountRegates(records, goal.Id.Value) < cap &&
               !records.Any(record => record.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate &&
                   record.GoalId == goal.Id.Value && record.EvidenceKind == EvidenceKind(task) &&
                   string.Equals(record.CandidateSha, currentHead, StringComparison.OrdinalIgnoreCase));
    }

    internal static string ComposeText(string diagnosis, string workerResult) =>
        diagnosis + "\n\n--- Developer WORKER_RESULT (verbatim) ---\n" + workerResult;

    internal static bool RecordRegate(AcceptanceFailingTestIndex index, Goal goal, TaskSpec task,
        AdjudicateOperatorIntentPayload payload, string head, DateTimeOffset now)
    {
        var before = AcceptanceFailingTestIndex.CountRegates(index.Read(), goal.Id.Value);
        var record = new AcceptanceFailingTestIndexRecord(AcceptanceFailingTestIndex.ContractVersion,
            AcceptanceFailingTestIndexKinds.ApparatusRegate, goal.Id.Value, now, CandidateSha: head,
            TestIdentity: string.Join(", ", (payload.EvidenceReferences ?? [])
                .Where(reference => reference.StartsWith("failing-test=", StringComparison.Ordinal))
                .Select(reference => reference["failing-test=".Length..])),
            EvidenceKind: EvidenceKind(task));
        index.Append([record], now);
        var after = index.Read();
        return after.Contains(record) && AcceptanceFailingTestIndex.CountRegates(after, goal.Id.Value) == before + 1;
    }

    private static string EvidenceKind(TaskSpec task) => $"steward-case-d:{task.Id.Value}";
}
