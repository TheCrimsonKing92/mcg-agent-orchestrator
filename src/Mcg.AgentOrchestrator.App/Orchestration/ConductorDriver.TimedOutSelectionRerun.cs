using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private sealed record TimedOutRoundReceipt(TaskSpec Owner, ReviewFinding Finding,
        FindingEvidenceReceipt Receipt, FocusedEvidenceRunResult Evidence);

    private FailedGoalTimedOutSelectionFacts? BuildTimedOutSelectionFacts(Goal goal, TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Failed ||
            TesterInconclusiveRoundInputsReader.Read(goal, task) is not { InputsUnchanged: true } rounds)
            return null;
        var receipts = ReadTimedOutRoundReceipts(goal, task, rounds);
        if (receipts is null) return null;
        return new(receipts.SelectMany(item => AllRoundChecks(item.Evidence))
            .Where(check => !check.Passed)
            .Select(check => new FailedGoalRoundCheck(check.Name, CheckClassification(check))).ToImmutableArray(),
            FailedGoalTimedOutSelectionRerunRule.ReadRecordedKeys(goal));
    }

    private TimedOutRoundReceipt[]? ReadTimedOutRoundReceipts(
        Goal goal, TaskSpec tester, FailedGoalInconclusiveRoundPair rounds)
    {
        var currentSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!ConductorGitRevisionReader.IsValid(currentSha) ||
            tester.LastVerification?.CandidateIdentity?.Canonical != rounds.Current.CandidateIdentity) return null;
        var result = new List<TimedOutRoundReceipt>();
        foreach (var id in rounds.Current.ReceiptIds)
        {
            var owner = goal.Tasks.FirstOrDefault(task => task.VerificationHistory.Any(record =>
                (record.FindingEvidenceReceipts ?? []).Any(receipt => receipt.ReceiptId == id)));
            var receipt = owner?.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
                .FirstOrDefault(item => item.ReceiptId == id);
            if (owner is null || receipt is null) return null;
            if (!string.Equals(receipt.CandidateSha, currentSha, StringComparison.OrdinalIgnoreCase)) continue;
            var finding = owner.LastVerification?.MergedReviewFindings?.FirstOrDefault(item =>
                item.EvidenceOutcome?.ReceiptId == id ||
                (receipt.RequestDispositions ?? []).Any(disposition => disposition.FindingStableId == item.StableId));
            var evidence = _focusedEvidenceAttemptCoordinator.ReadFocusedReceiptEvidence(goal.Id, id, receipt.CandidateSha);
            if (finding is null || evidence is null) return null;
            result.Add(new(owner, finding, receipt, evidence));
        }
        return result.Count == 0 ? null : result.ToArray();
    }

    private ConductorAdvanceResult? TryResumeTimedOutSelectionRerun(
        Goal goal, string prefix, ConductorAutonomyPolicy policy, GoalLifecycleState state,
        FailedGoalRecoveryFacts facts, FailedGoalRecoveryDecision escalation, IReadOnlyList<FailedGoalPendingNote> notes)
    {
        if (escalation.DiscriminatingEvidence != "verification-inconclusive-unchanged-inputs") return null;
        var task = facts.Tasks.Single(item => item.TaskId == escalation.Identity.TaskId);
        if (task.TimedOutSelections is not { } checks || task.InconclusiveRounds is not { } rounds) return null;
        // The request marker binds recovery to this batch. Resume its attempt, or replace only
        // a reconciled capacity deferral; missing artifacts cannot authorize another launch.
        var resume = FailedGoalTimedOutSelectionRerunRule.TryDecide(facts,
            task with { TimedOutSelections = checks with { CompletedRerunKeys = [] } }, rounds);
        if (resume?.TimedOutRerun is not { } rerun) return null;
        var reason = FailedGoalTimedOutSelectionRerunRule.ReasonSlug;
        if (!goal.Timeline.Any(evt => evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.StartsWith($"{reason}; phase=request; key={rerun.Key};", StringComparison.Ordinal))) return null;
        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!ConductorGitRevisionReader.IsValid(candidateSha)) return null;
        var receiptEvent = goal.Timeline.LastOrDefault(evt => evt.TaskId == task.TaskId &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith($"{reason}; phase=receipt; key={rerun.Key};", StringComparison.Ordinal));
        if (receiptEvent is not null)
        {
            if (receiptEvent.Message.EndsWith("outcome=timed-out", StringComparison.Ordinal) ||
                goal.Timeline.SkipWhile(evt => !ReferenceEquals(evt, receiptEvent)).Any(evt =>
                    evt.TaskId == task.TaskId && evt.Kind == ProgressKind.TaskRetried)) return null;
            var receiptId = CreateFindingEvidenceReceiptId(candidateSha!, reason, rerun.Key);
            var evidence = _focusedEvidenceAttemptCoordinator.ReadFocusedReceiptEvidence(goal.Id, receiptId, candidateSha!);
            if (evidence is null || AllRoundChecks(evidence).Any(check => !check.Passed &&
                CheckClassification(check) == FailedGoalTimedOutSelectionRerunRule.TimedOutClassification)) return null;
            if (!IsFailedGoalRecoveryContextCurrent(goal, policy, state, resume, out var staleReason))
                return MakeResult(goal.Id.Value, prefix, policy, new ConductorAdvanceOutcome.Held(state, staleReason));
            return RetryTesterAfterTimedOutRerun(goal, prefix, policy, state, task.TaskId);
        }
        var batch = CreateFindingEvidenceBatchId(candidateSha!, reason, reason, rerun.Key);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], candidateSha, null);
        var attempt = _focusedEvidenceAttemptCoordinator.ReadLatestFocusedEvidenceAttempt(candidate, batch);
        if (attempt is null) return null;
        if (attempt.ReconciledAt is not null)
        {
            if (attempt.Outcome is not (ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot or
                ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock or
                ConductorParallelAcceptanceAttemptOutcome.Cancelled or
                ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred or
                ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable or
                ConductorParallelAcceptanceAttemptOutcome.StaleCandidate)) return null;
            return ExecuteTimedOutSelectionRerun(goal, prefix, policy, state, resume, notes);
        }
        return ExecuteTimedOutSelectionRerun(goal, prefix, policy, state, resume, notes, attempt);
    }

    private ConductorAdvanceResult ExecuteTimedOutSelectionRerun(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, GoalLifecycleState state,
        FailedGoalRecoveryDecision decision, IReadOnlyList<FailedGoalPendingNote> pendingNotes,
        ConductorParallelAcceptanceAttempt? existingAttempt = null)
    {
        var rerun = decision.TimedOutRerun ?? throw new InvalidOperationException("Timeout rerun action has no selection set.");
        var tester = goal.Tasks.Single(task => task.Id == decision.Identity.TaskId);
        var rounds = TesterInconclusiveRoundInputsReader.Read(goal, tester);
        var sources = rounds is null ? null : ReadTimedOutRoundReceipts(goal, tester, rounds);
        if (sources is null || !TryMapTimedOutSelections(sources, rerun, out var selections))
        {
            ApplyPendingFailedGoalNotes(goal, pendingNotes);
            _recordTaskNote(goal.Id, tester.Id,
                $"{FailedGoalTimedOutSelectionRerunRule.ReasonSlug}; declined: current receipts or exact selection mapping unavailable.");
            return EscalateUnchangedTimedOutRound(goal, goalPrefix, policy, state, tester, rounds);
        }
        if (!IsFailedGoalRecoveryContextCurrent(goal, policy, state, decision, out var staleReason))
            return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Held(state, staleReason));
        ApplyPendingFailedGoalNotes(goal, pendingNotes);
        var reason = FailedGoalTimedOutSelectionRerunRule.ReasonSlug;
        var candidateSha = sources[0].Receipt.CandidateSha;
        var request = string.Join("; ", selections.Select(FormatFindingEvidenceSelection));
        var typedRequest = new FindingEvidenceRequest(selections);
        var requestIdentity = BuildFindingEvidenceIdentity(typedRequest);
        var dispositions = sources.Select(source => new FindingEvidenceRequestDisposition(
            source.Finding.StableId, requestIdentity, "executed", reason)).Distinct().ToArray();
        // The key, rather than the Tester attempt or policy name, binds adoption across relaunches.
        var context = new ConductorFocusedEvidenceRequestContext(reason,
            CreateFindingEvidenceBatchId(candidateSha, reason, reason, rerun.Key), dispositions);
        var complete = TryReconcileFocusedEvidenceAttempt(goal, policy, request, candidateSha, reason, context,
            out var evidence, out var attempt, out var observation, out var kind, existingAttempt);
        if (attempt?.FocusedEvidenceBatchId == context.BatchId &&
            (complete || kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or ConductorParallelAcceptanceAttemptDecisionKind.Running))
        {
            var marker = $"{reason}; phase=request; key={rerun.Key};";
            if (!goal.Timeline.Any(evt => evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                evt.Message.StartsWith(marker, StringComparison.Ordinal)))
                _recordFindingEvidenceRequest(goal.Id, tester.Id,
                    $"{marker} candidate={Uri.EscapeDataString(rerun.CandidateIdentity)}; selections={string.Join(',', rerun.Selections)}");
        }
        if (!complete)
        {
            if (observation.Kind == FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired)
                return Escalate(goal, goalPrefix, policy, state, observation.Evidence);
            return MakeResult(goal.Id.Value, goalPrefix, policy, FocusedEvidencePendingHeld(state, observation, kind));
        }
        var receiptId = CreateFindingEvidenceReceiptId(candidateSha, reason, rerun.Key);
        var receipt = new FindingEvidenceReceipt(receiptId, candidateSha, typedRequest, evidence.Accepted,
            evidence.IsValidEvidence, evidence.Summary, (evidence.Arms ?? []).Select(CreateFindingEvidenceArmReceipt).ToArray(),
            dispositions, reason, requestIdentity);
        foreach (var source in sources.DistinctBy(item => (item.Owner.Id, item.Finding.StableId)))
            _recordFindingEvidenceOutcome(goal.Id, source.Owner.Id, source.Finding.StableId,
                new FindingEvidenceOutcome(evidence.Accepted, receiptId, Detail: evidence.Summary,
                    ResultReason: evidence.OutcomeReason), receipt);
        if (attempt is null || !_focusedEvidenceAttemptCoordinator.RecordFocusedEvidenceRequestDispositions(
            attempt, reason, receiptId, dispositions))
            throw new InvalidOperationException($"Could not persist timeout rerun receipt {receiptId}.");
        var timedOut = AllRoundChecks(evidence).Any(check => !check.Passed &&
            CheckClassification(check) == FailedGoalTimedOutSelectionRerunRule.TimedOutClassification);
        _recordFindingEvidenceRun(goal.Id, tester.Id,
            $"{reason}; phase=receipt; key={rerun.Key}; receipt_id={receiptId}; outcome={(timedOut ? "timed-out" : evidence.Passed ? "passed" : "failed")}");
        _focusedEvidenceAttemptCoordinator.MarkReconciled(attempt);
        if (timedOut) return EscalateUnchangedTimedOutRound(GetCurrentGoal(goal), goalPrefix, policy, state, tester, rounds);
        return RetryTesterAfterTimedOutRerun(goal, goalPrefix, policy, state, tester.Id);
    }

    private ConductorAdvanceResult RetryTesterAfterTimedOutRerun(Goal goal, string goalPrefix,
        ConductorAutonomyPolicy policy, GoalLifecycleState state, TaskId testerId)
    {
        _retryTask(goal.Id, testerId, FailedGoalTimedOutSelectionRerunRule.ReasonSlug,
            null, RetryCause.EnvironmentApparatusFailure);
        var refreshed = GetCurrentGoal(goal);
        if (refreshed.Tasks.Single(task => task.Id == testerId).Status is not (WorkTaskStatus.Assigned or WorkTaskStatus.Pending) ||
            refreshed.Tasks.Any(task => task.LastProcess is { IsRunning: true }))
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, "Timeout rerun retry changed dispatch authority; re-observe on next tick."));
        return ExecuteDispatchAndStart(refreshed, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
    }

    private static bool TryMapTimedOutSelections(TimedOutRoundReceipt[] sources,
        FailedGoalTimedOutSelectionRerun rerun, out FindingEvidenceSelection[] selections)
    {
        var mapped = new List<FindingEvidenceSelection>();
        foreach (var slug in rerun.Selections)
        {
            var matches = new List<FindingEvidenceSelection>();
            foreach (var source in sources)
            foreach (var target in source.Evidence.Coverage?.TargetToChecks ?? [])
            {
                if (!target.CheckNames.Any(name => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') == slug))
                    continue;
                matches.AddRange(source.Receipt.Request.Selections.Where(selection =>
                    string.Equals(FormatFindingEvidenceSelection(selection), target.Target, StringComparison.OrdinalIgnoreCase)));
            }
            var exact = matches.DistinctBy(FormatFindingEvidenceSelection).ToArray();
            if (exact.Length == 0) { selections = []; return false; }
            mapped.AddRange(exact);
        }
        selections = mapped.DistinctBy(FormatFindingEvidenceSelection)
            .OrderBy(FormatFindingEvidenceSelection, StringComparer.Ordinal).ToArray();
        return selections.Length > 0;
    }

    private ConductorAdvanceResult EscalateUnchangedTimedOutRound(Goal goal, string prefix,
        ConductorAutonomyPolicy policy, GoalLifecycleState state, TaskSpec tester, FailedGoalInconclusiveRoundPair? rounds) =>
        Escalate(goal, prefix, policy, state,
            $"Tester task {ShortTaskId(tester.Id)} stayed verification-inconclusive on unchanged inputs; operator action required. {rounds?.DescribeUnchanged(DispatchFailureClassifier.Classify(tester, tester.LastVerification!).EvidenceSummary)}");

    private static IReadOnlyList<AcceptanceCheckResult> AllRoundChecks(FocusedEvidenceRunResult evidence) =>
        evidence.Checks.Concat((evidence.Arms ?? []).SelectMany(arm => arm.Checks)).ToArray();

    private static string? CheckClassification(AcceptanceCheckResult check) =>
        check.FailureClassification;
}
