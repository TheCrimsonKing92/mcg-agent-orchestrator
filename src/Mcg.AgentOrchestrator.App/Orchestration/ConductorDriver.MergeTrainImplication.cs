using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal IReadOnlySet<string> ReadTrainImplicatedMemberKeys() =>
        _mergeTrainAcceptanceStore?.ReadTrainImplicatedCandidateKeys() ??
        new HashSet<string>(StringComparer.Ordinal);

    internal IReadOnlySet<string> ReadSuppressedGroupedPairs()
    {
        var pairs = new HashSet<string>(ReadSuppressedCohortPairs(), StringComparer.Ordinal);
        if (_mergeTrainAcceptanceStore is not null)
            pairs.UnionWith(_mergeTrainAcceptanceStore.ReadSuppressedPairs());
        return pairs;
    }

    private static bool IsGenuineTrainRed(MergeTrainReceipt receipt, MergeTrainMemberBinding? attributed = null)
    {
        if (receipt.Outcome != MergeTrainGateOutcome.Failed) return false;
        if (receipt.FailedChecks.Contains(SourceSizeRatchetPreflight.CheckName, StringComparer.Ordinal))
            return true;

        // An absent apparatus signature alone is not failure evidence. Require a fatal test
        // result; unreadable/missing TRX and all-apparatus failures cannot implicate a tree.
        var failures = MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths);
        return failures.Count != 0 &&
            (!failures.Any(MergeTrainRedAttribution.IsMessageSubjectGuard) || attributed is not null);
    }

    private void RecordTrainImplicatedMember(
        MergeTrainMemberBinding dropped, MergeTrainReceipt redReceipt, IReadOnlyList<string> subjects)
    {
        if (!IsGenuineTrainRed(redReceipt, subjects.Count == 0 ? null : dropped)) return;
        _mergeTrainAcceptanceStore!.RecordTrainImplicatedCandidate(
            dropped.GoalId, dropped.CandidateRevision, redReceipt.Identity.Value,
            redReceipt.Identity.ObservedMainRevision);
        if (subjects.Count == 0 || _cohortWorkspace is null) return;
        try
        {
            var writer = new ConductEventLogWriter(_cohortWorkspace.ConductEventsLogPath);
            TryAppendGateProgressEvent(writer, dropped.GoalId.Value,
                $"TRAIN_IMPLICATED train={redReceipt.Identity.Value} member={dropped.GoalId.Value} " +
                $"candidate_revision={dropped.CandidateRevision} subjects={string.Join(',', subjects)}",
                eventKind: "train-attribution");
        }
        catch (Exception) { /* Event-log setup is observational. */ }
    }

    private void RecordTrainRedPair(
        ConductorMergeTrainSelection selection,
        IReadOnlyList<MergeTrainMemberBinding> members,
        MergeTrainReceipt receipt,
        MergeTrainMemberBinding? attributed)
    {
        if (members.Count != 2 || !IsGenuineTrainRed(receipt, attributed)) return;
        var first = selection.Members.Single(member => member.GoalId == members[0].GoalId);
        var second = selection.Members.Single(member => member.GoalId == members[1].GoalId);
        // The existing fingerprint is ordered. Persist both orientations so a reordered
        // Ready batch cannot re-admit the same revisions at the same main revision.
        _mergeTrainAcceptanceStore!.SuppressPair(
            ConductorAcceptanceCohortSelector.PairFingerprint(first, second), receipt.Identity.Value);
        _mergeTrainAcceptanceStore.SuppressPair(
            ConductorAcceptanceCohortSelector.PairFingerprint(second, first), receipt.Identity.Value);
    }
}
