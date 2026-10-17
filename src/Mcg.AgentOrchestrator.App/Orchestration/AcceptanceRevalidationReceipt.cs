using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AcceptanceRevalidationReceipt(
    string SourceReceiptId,
    string SourceMainSha,
    string CurrentMainSha,
    string Outcome,
    string Reason,
    IReadOnlyList<string> AffectedChecks,
    IReadOnlyList<string> UnaffectedChecks,
    int LandedPathCount,
    string? RevalidationReceiptId)
{
    internal const string Operation = "conductor:acceptance-revalidation";

    internal string Format()
    {
        var affectedCount = AffectedChecks.Count;
        var unaffectedCount = UnaffectedChecks.Count;
        string Render() => $"ACCEPTANCE_REVALIDATION source={SourceReceiptId} sourceMain={SourceMainSha} " +
            $"currentMain={CurrentMainSha} outcome={Outcome} reason={Reason} landed={LandedPathCount} " +
            $"affected={EncodeNames(AffectedChecks, affectedCount)} unaffected={EncodeNames(UnaffectedChecks, unaffectedCount)} " +
            $"revalidationReceipt={RevalidationReceiptId ?? "none"}";
        var line = Render();
        while (line.Length > 1024 && (affectedCount > 0 || unaffectedCount > 0))
        {
            if (affectedCount > 0) affectedCount--;
            else unaffectedCount--;
            line = Render();
        }
        // Receipt ids and revisions are bounded by their source stores. Never cut a fixed field.
        if (line.Length > 1024)
            throw new InvalidOperationException("Revalidation receipt fixed fields exceed 1024 characters.");
        return line;
    }

    internal static string EncodeNames(IReadOnlyList<string> names) => EncodeNames(names, names.Count);

    private static string EncodeNames(IReadOnlyList<string> names, int count) => string.Join(',',
        names.Take(count).Select(Uri.EscapeDataString).Concat(count < names.Count ? [$"+{names.Count - count}"] : []));

    internal static bool TryParse(string? line, out AcceptanceRevalidationReceipt? receipt)
    {
        receipt = null;
        if (line is null || line.Length > 1024 || !line.StartsWith("ACCEPTANCE_REVALIDATION ", StringComparison.Ordinal))
            return false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in line[24..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf('=');
            if (separator < 1 || !fields.TryAdd(token[..separator], token[(separator + 1)..]))
                return false;
        }
        foreach (var key in new[] { "source", "sourceMain", "currentMain", "outcome", "reason", "landed", "affected", "unaffected", "revalidationReceipt" })
            if (!fields.ContainsKey(key)) return false;
        if (string.IsNullOrWhiteSpace(fields["source"]) || string.IsNullOrWhiteSpace(fields["sourceMain"]) ||
            string.IsNullOrWhiteSpace(fields["currentMain"]) || string.IsNullOrWhiteSpace(fields["reason"]) ||
            fields["outcome"] is not ("pending" or "landed" or "regate") ||
            !int.TryParse(fields["landed"], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            return false;
        static string[] Names(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(name => !(name.StartsWith('+') && int.TryParse(name[1..], out _)))
            .Select(Uri.UnescapeDataString).ToArray();
        receipt = new(fields["source"], fields["sourceMain"], fields["currentMain"], fields["outcome"], fields["reason"],
            Names(fields["affected"]), Names(fields["unaffected"]), count,
            fields["revalidationReceipt"] == "none" ? null : fields["revalidationReceipt"]);
        return true;
    }

    internal static bool TryReport(MergeTrainReceipt source, string moved,
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        PassedMergeTrainReceiptAdmission.Operations operations, int tick, Action<string> emitProgress)
    {
        if (moved.StartsWith("member:", StringComparison.Ordinal) || operations.PlanRevalidation is null)
            return false;
        try
        {
            var currentMain = candidates.Where(candidate => source.Identity.Members.Any(member => member.GoalId == candidate.GoalId))
                .Select(candidate => candidate.ProjectionResult).OfType<GateReadyCandidateProjectionResult.Ready>()
                .Select(ready => ready.Projection.MainRevision).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(currentMain)) return false;
            var plan = operations.PlanRevalidation(source, currentMain);
            AcceptanceRevalidationReceipt receipt;
            switch (plan)
            {
                case AcceptanceRevalidationPlan.Revalidate revalidate:
                    receipt = new(source.ReceiptId, source.Identity.ObservedMainRevision, currentMain, "pending", "disjoint",
                        revalidate.AffectedChecks, revalidate.UnaffectedChecks, revalidate.LandedPaths.Count, null);
                    operations.RecordRevalidation?.Invoke(source, receipt);
                    emitProgress($"TRAIN_RECEIPT_REVALIDATE tick={tick} train={source.Identity.Value} receipt={source.ReceiptId} " +
                        $"sourceMain={receipt.SourceMainSha} currentMain={currentMain} " +
                        $"affected={EncodeNames(receipt.AffectedChecks)} unaffected={EncodeNames(receipt.UnaffectedChecks)}");
                    return true;
                case AcceptanceRevalidationPlan.FullRegate regate:
                    receipt = new(source.ReceiptId, source.Identity.ObservedMainRevision, currentMain, "regate", regate.Reason, [], [], 0, null);
                    operations.RecordRevalidation?.Invoke(source, receipt);
                    break;
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Advisory bookkeeping cannot change ordinary admission or its stale fallback.
        }
        return false;
    }

    // A null decision means the passed train has just landed; correlate its pending journal entries.
    internal static void Record(string executionDirectory, IReadOnlyList<Goal> scopedGoals,
        MergeTrainReceipt train, AcceptanceRevalidationReceipt? decision)
    {
        foreach (var goal in scopedGoals.Where(goal => train.Identity.Members.Any(member => member.GoalId == goal.Id)))
        {
            try
            {
                if (decision is not null)
                {
                    if (goal.Status != GoalStatus.Completed) Write(executionDirectory, goal, decision);
                    continue;
                }
                if (goal.Status != GoalStatus.Completed) continue;
                var pending = GoalOperationJournal.Read(executionDirectory, goal.Id).Entries
                    .Where(entry => entry.Operation == Operation && entry.Status == GoalOperationStatus.Completed)
                    .OrderByDescending(entry => entry.At)
                    .Select(entry => TryParse(entry.Detail, out var parsed) ? parsed : null)
                    .Where(parsed => parsed is { Outcome: "pending" } &&
                        string.Equals(parsed.CurrentMainSha, train.Identity.ObservedMainRevision, StringComparison.OrdinalIgnoreCase));
                var store = MergeTrainAcceptanceStore.OpenReadOnly(Path.Combine(
                    OrchestratorWorkspace.ForDirectory(executionDirectory).OrchestratorDirectory, "merge-train-acceptance.db"));
                var sources = store.ReadPassedReceiptsForGoal(goal.Id);
                foreach (var prior in pending)
                {
                    var source = sources.SingleOrDefault(source => source.ReceiptId == prior!.SourceReceiptId);
                    if (source is null || source.ReceiptId == train.ReceiptId ||
                        !string.Equals(source.Identity.ObservedMainRevision, prior!.SourceMainSha, StringComparison.OrdinalIgnoreCase) ||
                        !SameCandidates(source, train)) continue;
                    Write(executionDirectory, goal, prior with
                    {
                        Outcome = "landed", Reason = "revalidated", RevalidationReceiptId = train.ReceiptId
                    });
                    break;
                }
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                // A missing journal/store or unmatched source supplies no landing evidence.
            }
        }
    }

    private static bool SameCandidates(MergeTrainReceipt source, MergeTrainReceipt landed) =>
        source.Identity.Members.Count == landed.Identity.Members.Count && source.Identity.Members.All(member =>
            landed.Identity.Members.Any(other => other.GoalId == member.GoalId &&
                string.Equals(other.CandidateRevision, member.CandidateRevision, StringComparison.OrdinalIgnoreCase)));

    private static void Write(string directory, Goal goal, AcceptanceRevalidationReceipt receipt) =>
        GoalOperationJournal.Completed(directory, goal, Operation, receipt.Format(), mainHeadSha: receipt.CurrentMainSha);
}
