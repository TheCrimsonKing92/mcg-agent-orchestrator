using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly HashSet<string> _resolvedMissingBaselineRequests = new(StringComparer.Ordinal);

    private bool TryResumeUnconfirmedCandidateRed(
        Goal goal,
        TaskSpec requestingTask,
        ConductorAutonomyPolicy policy,
        FindingEvidenceBatch batch,
        string candidateSha,
        string findingRoundFingerprint,
        ConductorFocusedEvidenceRequestContext requestContext,
        out FailedGoalFindingObservation decision)
    {
        decision = FailedGoalFindingObservation.None;
        var guard = $"candidate_sha={candidateSha}; finding_round={findingRoundFingerprint}; " +
                    $"request_identity={BuildFindingEvidenceRequestIdentity(batch.Findings)}";
        var recorded = goal.Timeline.LastOrDefault(evt =>
                evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                evt.TaskId == requestingTask.Id &&
                evt.Message.Contains("disposition=baseline-arm-absent;", StringComparison.Ordinal) &&
                evt.Message.Contains(guard, StringComparison.Ordinal));
        if (recorded is null)
        {
            return false;
        }

        const string marker = "; receipt_id=";
        var receiptStart = recorded.Message.IndexOf(marker, StringComparison.Ordinal);
        if (receiptStart < 0)
            throw new InvalidDataException("Recorded baseline-arm-absent decision has no candidate receipt id.");
        receiptStart += marker.Length;
        var receiptEnd = recorded.Message.IndexOf(';', receiptStart);
        var receiptId = recorded.Message[receiptStart..(receiptEnd < 0 ? recorded.Message.Length : receiptEnd)];
        if (string.IsNullOrWhiteSpace(receiptId))
            throw new InvalidDataException("Recorded baseline-arm-absent decision has an empty candidate receipt id.");
        RouteUnconfirmedCandidateRed(
            goal, requestingTask, policy, batch, candidateSha, findingRoundFingerprint,
            requestContext, receiptId, guard, out decision);
        return true;
    }

    private void MarkFocusedEvidenceAttemptIfReady(
        ConductorFocusedEvidenceRequestContext? requestContext,
        ConductorParallelAcceptanceAttemptDecision attemptDecision)
    {
        var deferRerunReconciliation =
            requestContext?.BatchId.EndsWith("-candidate-rerun", StringComparison.Ordinal) == true &&
            (attemptDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed &&
             attemptDecision.Run?.Exception is not (
                 DotnetBuildSlotsBusyException or BuildLockBlockedException or OperationCanceledException) ||
             attemptDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun &&
             IsFocusedEvidenceExecutionFault(attemptDecision.Attempt.Outcome));
        if ((requestContext?.RunBaselineArm == true || deferRerunReconciliation) &&
            attemptDecision.Run?.Exception is not (
                DotnetBuildSlotsBusyException or BuildLockBlockedException or OperationCanceledException) &&
            attemptDecision.Attempt.Outcome is not (
                ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot or
                ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock or
                ConductorParallelAcceptanceAttemptOutcome.Cancelled or
                ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred or
                ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable or
                ConductorParallelAcceptanceAttemptOutcome.StaleCandidate))
        {
            return;
        }

        _focusedEvidenceAttemptCoordinator.MarkReconciled(attemptDecision.Attempt);
    }

    private static bool IsFocusedEvidenceExecutionFault(ConductorParallelAcceptanceAttemptOutcome outcome) =>
        outcome is
            ConductorParallelAcceptanceAttemptOutcome.ProcessDied or
            ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts or
            ConductorParallelAcceptanceAttemptOutcome.LaunchFailed or
            ConductorParallelAcceptanceAttemptOutcome.Faulted or
            ConductorParallelAcceptanceAttemptOutcome.GateEngineFault;

    private bool TryRestorePendingBaselineCandidate(
        Goal goal,
        string request,
        string candidateSha,
        ConductorFocusedEvidenceRequestContext requestContext,
        out FocusedEvidenceRunResult evidence,
        out ConductorParallelAcceptanceAttempt? evidenceAttempt)
    {
        evidence = null!;
        evidenceAttempt = _focusedEvidenceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value])
            .LastOrDefault(attempt =>
                attempt.FocusedEvidenceRunsBaselineArm &&
                string.Equals(attempt.BranchHeadSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(attempt.FindingRoundFingerprint, requestContext.FindingRoundFingerprint, StringComparison.Ordinal) &&
                string.Equals(attempt.FocusedEvidenceBatchId, requestContext.BatchId + "-baseline-arm", StringComparison.Ordinal) &&
                string.Equals(attempt.FocusedEvidenceRequest, request, StringComparison.Ordinal));
        if (evidenceAttempt is null)
        {
            return false;
        }

        evidence = evidenceAttempt.CandidateEvidenceBeforeBaseline ??
            throw new InvalidDataException(
                $"Baseline attempt {evidenceAttempt.AttemptId} has no saved candidate evidence.");
        return true;
    }

    private bool TryResolveMissingBaseline(
        Goal goal,
        TaskSpec requestingTask,
        ConductorAutonomyPolicy policy,
        FindingEvidenceBatch batch,
        string candidateSha,
        string findingRoundFingerprint,
        ConductorFocusedEvidenceRequestContext requestContext,
        FocusedEvidenceRunResult initialEvidence,
        IReadOnlyList<FindingEvidenceArmReceipt> initialArms,
        out FocusedEvidenceRunResult evidence,
        out IReadOnlyList<FindingEvidenceArmReceipt> arms,
        out string receiptIdentity,
        out FailedGoalFindingObservation decision,
        string? existingCandidateReceiptId = null)
    {
        evidence = initialEvidence;
        arms = initialArms;
        receiptIdentity = batch.Identity;
        decision = FailedGoalFindingObservation.None;
        if (!IsCandidateOnlyRed(candidateSha, initialArms, out var candidateOnlyRed) ||
            EveryFailingTestIsInsideCandidateChanges(
                goal, batch, candidateOnlyRed.FailingTestIdentities ?? []))
        {
            return true;
        }

        var candidateReceiptId = existingCandidateReceiptId ?? CreateFindingEvidenceReceiptId(
            candidateSha, findingRoundFingerprint, receiptIdentity);
        var requestIdentity = BuildFindingEvidenceRequestIdentity(batch.Findings);
        var guard = $"candidate_sha={candidateSha}; finding_round={findingRoundFingerprint}; request_identity={requestIdentity}";
        if (_resolvedMissingBaselineRequests.Contains(guard) || goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                evt.Message.Contains("disposition=baseline-arm-absent;", StringComparison.Ordinal) &&
                evt.Message.Contains(guard, StringComparison.Ordinal)))
        {
            return RouteUnconfirmedCandidateRed(
                goal, requestingTask, policy, batch, candidateSha, findingRoundFingerprint,
                requestContext, candidateReceiptId, guard, out decision);
        }

        var baselineContext = requestContext with
        {
            BatchId = requestContext.BatchId + "-baseline-arm",
            RunBaselineArm = true,
            NegativeControl = null,
            CandidateEvidenceBeforeBaseline = initialEvidence
        };
        if (!TryReconcileFocusedEvidenceAttempt(
                goal, policy, batch.Request, candidateSha, "finding-baseline-arm", baselineContext,
                out var baselineEvidence, out var baselineAttempt, out decision, out var attemptKind))
        {
            if (attemptKind is null or ConductorParallelAcceptanceAttemptDecisionKind.Started or
                    ConductorParallelAcceptanceAttemptDecisionKind.Running || baselineAttempt is null ||
                !IsFocusedEvidenceExecutionFault(baselineAttempt.Outcome))
            {
                return false;
            }
            return RecordMissingBaselineAndRoute(
                goal, requestingTask, policy, batch, candidateSha, findingRoundFingerprint,
                requestContext, requestIdentity,
                candidateReceiptId, initialEvidence, initialArms, baselineAttempt, baselineAttempt.ResultPath,
                $"{baselineAttempt.Outcome}: {baselineAttempt.Detail ?? decision.Evidence}", out decision);
        }

        var baselineArms = (baselineEvidence.Arms ?? []).Select(CreateFindingEvidenceArmReceipt).ToArray();
        receiptIdentity += ":baseline-arm";
        var baselineReceiptId = CreateFindingEvidenceReceiptId(
            candidateSha, findingRoundFingerprint, receiptIdentity);
        var baseline = baselineArms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        if (baseline is null || baseline.Disposition is
                FindingEvidenceArmDisposition.ApparatusFailure or FindingEvidenceArmDisposition.Inconclusive)
        {
            var reason = baseline is null
                ? "baseline arm absent from focused evidence result"
                : $"baseline arm {baseline.Disposition}: {baseline.Summary}";
            return RecordMissingBaselineAndRoute(
                goal, requestingTask, policy, batch, candidateSha, findingRoundFingerprint,
                requestContext, requestIdentity,
                candidateReceiptId, initialEvidence, initialArms, baselineAttempt!, baselineAttempt?.ResultPath,
                reason, out decision);
        }

        evidence = RetainNegativeControlEvidence(baselineEvidence, initialEvidence);
        arms = initialEvidence.NegativeControlOutcome is null ? baselineArms :
            [.. baselineArms, .. initialArms.Where(arm => arm.Arm == FindingEvidenceArm.SourceReverted)];
        _focusedEvidenceAttemptCoordinator.MarkReconciled(baselineAttempt!);
        return true;
    }

    private bool RecordMissingBaselineAndRoute(
        Goal goal,
        TaskSpec requestingTask,
        ConductorAutonomyPolicy policy,
        FindingEvidenceBatch batch,
        string candidateSha,
        string findingRoundFingerprint,
        ConductorFocusedEvidenceRequestContext requestContext,
        string requestIdentity,
        string receiptId,
        FocusedEvidenceRunResult candidateEvidence,
        IReadOnlyList<FindingEvidenceArmReceipt> candidateArms,
        ConductorParallelAcceptanceAttempt baselineAttempt,
        string? resultPath,
        string reason,
        out FailedGoalFindingObservation decision)
    {
        var guard = $"candidate_sha={candidateSha}; finding_round={findingRoundFingerprint}; request_identity={requestIdentity}";
        var receipt = new FindingEvidenceReceipt(
            receiptId, candidateSha, batch.TypedRequest, candidateEvidence.Accepted,
            candidateEvidence.IsValidEvidence, candidateEvidence.Summary, candidateArms,
            FindingRoundFingerprint: findingRoundFingerprint,
            NegativeControlOutcome: candidateEvidence.NegativeControlOutcome);
        foreach (var finding in batch.Findings)
        {
            _recordFindingEvidenceOutcome(
                goal.Id, requestingTask.Id, finding.StableId,
                new FindingEvidenceOutcome(
                    Honoured: true, ReceiptId: receiptId,
                    Detail: $"Baseline arm absent: {reason}",
                    ResultReason: FindingEvidenceOutcomeReason.CandidateRed,
                    RequestedSelectionIdentity: requestIdentity,
                    DecisionReason: "baseline-arm-absent"),
                receipt);
        }

        _recordFindingEvidenceRequest(
            goal.Id, requestingTask.Id,
            $"finding-evidence disposition=baseline-arm-absent; {guard}; receipt_id={receiptId}; " +
            $"result_path={resultPath ?? "none"}; reason={TrimForConductorMessage(reason)}");
        TryRaiseMissingBaselineNotice(goal, guard, receiptId, resultPath, reason);
        _resolvedMissingBaselineRequests.Add(guard);
        _focusedEvidenceAttemptCoordinator.MarkReconciled(baselineAttempt);
        return RouteUnconfirmedCandidateRed(
            goal, requestingTask, policy, batch, candidateSha, findingRoundFingerprint,
            requestContext, receiptId, guard, out decision);
    }

    private bool RouteUnconfirmedCandidateRed(
        Goal goal,
        TaskSpec requestingTask,
        ConductorAutonomyPolicy policy,
        FindingEvidenceBatch batch,
        string candidateSha,
        string findingRoundFingerprint,
        ConductorFocusedEvidenceRequestContext requestContext,
        string originalReceiptId,
        string guard,
        out FailedGoalFindingObservation decision)
    {
        var rerunGuard = $"disposition=candidate-rerun-requested; {guard}";
        var terminal = goal.Timeline.LastOrDefault(evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.TaskId == requestingTask.Id &&
            evt.Message.Contains(guard, StringComparison.Ordinal) &&
            (evt.Message.Contains("disposition=candidate-rerun-green;", StringComparison.Ordinal) ||
             evt.Message.Contains("disposition=candidate-rerun-red;", StringComparison.Ordinal) ||
             evt.Message.Contains("disposition=candidate-rerun-unusable;", StringComparison.Ordinal)));
        if (terminal is not null)
        {
            decision = terminal.Message.Contains("disposition=candidate-rerun-green;", StringComparison.Ordinal)
                ? BuildCandidateRerunGreenDecision(
                    goal, requestingTask, candidateSha, batch, requestContext,
                    CreateFindingEvidenceReceiptId(
                        candidateSha, findingRoundFingerprint, batch.Identity + ":candidate-rerun"))
                : BuildBaselineExecutionFailureEscalation(candidateSha, batch, originalReceiptId);
            return false;
        }

        if (!goal.Timeline.Any(evt => evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                                     evt.TaskId == requestingTask.Id &&
                                     evt.Message.Contains(rerunGuard, StringComparison.Ordinal)))
        {
            _recordFindingEvidenceRequest(goal.Id, requestingTask.Id,
                $"finding-evidence {rerunGuard}; original_receipt_id={originalReceiptId}");
        }

        var rerunContext = requestContext with
        {
            BatchId = requestContext.BatchId + "-candidate-rerun",
            RunBaselineArm = false,
            NegativeControl = null,
            CandidateEvidenceBeforeBaseline = null
        };
        if (!TryReconcileFocusedEvidenceAttempt(
                goal, policy, batch.Request, candidateSha, "finding-candidate-rerun", rerunContext,
                out var rerun, out var rerunAttempt, out decision, out var attemptKind))
        {
            if (attemptKind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun &&
                rerunAttempt is not null && IsFocusedEvidenceExecutionFault(rerunAttempt.Outcome) ||
                attemptKind == ConductorParallelAcceptanceAttemptDecisionKind.Completed &&
                decision.Kind == FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired)
            {
                _recordFindingEvidenceRequest(goal.Id, requestingTask.Id,
                    $"finding-evidence disposition=candidate-rerun-unusable; {guard}; " +
                    $"reason={TrimForConductorMessage(decision.Evidence)}");
                if (rerunAttempt is not null) _focusedEvidenceAttemptCoordinator.MarkReconciled(rerunAttempt);
                decision = BuildBaselineExecutionFailureEscalation(candidateSha, batch, originalReceiptId);
            }
            return false;
        }

        var rerunArms = (rerun.Arms ?? []).Select(CreateFindingEvidenceArmReceipt).ToArray();
        var candidate = rerunArms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var green = rerun is { Accepted: true, IsValidEvidence: true } && candidate is
        {
            Accepted: true,
            Passed: true,
            Disposition: FindingEvidenceArmDisposition.Green,
            ExecutedTestCount: > 0
        } && string.Equals(candidate.Sha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
            AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(
                candidate.TestResultPaths,
                candidate.ReceiptArtifacts,
                candidate.ExecutedTestCount!.Value,
                ResolveFindingEvidenceRequiredTestClasses(batch.TypedRequest));
        var confirmedRed = rerun.Accepted && candidate is
        {
            Accepted: true,
            Disposition: FindingEvidenceArmDisposition.Red,
            Passed: false,
            FailingTestIdentities.Count: > 0
        } && string.Equals(candidate.Sha, candidateSha, StringComparison.OrdinalIgnoreCase);
        var disposition = green ? "green" : confirmedRed ? "red" : "unusable";
        var rerunReceiptId = CreateFindingEvidenceReceiptId(
            candidateSha, findingRoundFingerprint, batch.Identity + ":candidate-rerun");
        if (green)
        {
            var receipt = new FindingEvidenceReceipt(
                rerunReceiptId, candidateSha, batch.TypedRequest, rerun.Accepted,
                rerun.IsValidEvidence, rerun.Summary, rerunArms,
                requestContext.RequestDispositions, findingRoundFingerprint,
                BuildFindingEvidenceExecutionBasisIdentity(_getFindingEvidenceEngineSettings(goal)));
            foreach (var finding in batch.Findings)
            {
                _recordFindingEvidenceOutcome(goal.Id, requestingTask.Id, finding.StableId,
                    new FindingEvidenceOutcome(
                        Honoured: true, ReceiptId: rerunReceiptId,
                        ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
                        RequestedSelectionIdentity: batch.Identity,
                        DecisionReason: "candidate-rerun-green",
                        SourceReceiptIds: [originalReceiptId, rerunReceiptId]), receipt);
            }
        }
        _recordFindingEvidenceRequest(goal.Id, requestingTask.Id,
            $"finding-evidence disposition=candidate-rerun-{disposition}; {guard}; " +
            $"receipt_id={rerunReceiptId}; original_receipt_id={originalReceiptId}");
        if (rerunAttempt is not null) _focusedEvidenceAttemptCoordinator.MarkReconciled(rerunAttempt);
        decision = green
            ? BuildCandidateRerunGreenDecision(
                goal, requestingTask, candidateSha, batch, requestContext, rerunReceiptId)
            : BuildBaselineExecutionFailureEscalation(candidateSha, batch, originalReceiptId);
        return false;
    }

    private FailedGoalFindingObservation BuildCandidateRerunGreenDecision(
        Goal goal, TaskSpec requestingTask, string candidateSha,
        FindingEvidenceBatch batch, ConductorFocusedEvidenceRequestContext requestContext,
        string receiptId) =>
        requestContext.RequestDispositions.Any(disposition =>
            disposition.Disposition.StartsWith("pending-", StringComparison.Ordinal))
            ? FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidencePending,
                "The candidate re-run was GREEN; another distinct request from the same finding round remains pending.")
            : BuildCappedFindingEvidenceDeliveryRetry(
                goal, requestingTask, candidateSha, batch.Findings, [receiptId],
                "The candidate re-run was GREEN after baseline execution failed.");

    private static FailedGoalFindingObservation BuildBaselineExecutionFailureEscalation(
        string candidateSha, FindingEvidenceBatch batch, string receiptId) =>
        FailedGoalFindingObservation.Observed(
            FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
            $"Baseline execution failure for candidate RED finding(s) " +
            $"{string.Join(',', batch.Findings.Select(finding => finding.StableId))} at candidate {candidateSha}; " +
            $"candidate_receipt_id={receiptId}; baseline_receipt_id={receiptId}. No worker was dispatched.");

    private void TryRaiseMissingBaselineNotice(
        Goal goal, string guard, string receiptId, string? resultPath, string reason)
    {
        if (string.IsNullOrWhiteSpace(_executionDirectory))
        {
            return;
        }

        var storeDirectory = Path.Combine(_executionDirectory, ".orchestrator");
        if (!Directory.Exists(storeDirectory))
        {
            return;
        }

        try
        {
            CollaborationItemStore.ForDirectory(storeDirectory).RaiseAsync(
                CollaborationItemType.Notice, goal.Id.Value,
                "Baseline arm absent from focused finding evidence",
                $"{guard}; receipt_id={receiptId}; result_path={resultPath ?? "none"}; reason={reason}. " +
                "Candidate RED will be re-run once at this candidate; no Developer was dispatched.",
                "finding-baseline-arm-absent:" + goal.Id.Value + ":" + receiptId,
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Baseline-arm notice failed after finding outcome was recorded: {guard}; receipt_id={receiptId}; {ex}");
        }
    }

    private static string BuildFindingEvidenceRequestIdentity(IReadOnlyList<ReviewFinding> findings)
    {
        var values = findings.OrderBy(finding => finding.StableId, StringComparer.Ordinal)
            .Select(finding => string.Join("\u001f", finding.StableId,
                string.Join("\u001e", (finding.EvidenceRequest?.Selections ?? [])
                    .Select(selection => $"{selection.TestProject}:{selection.TestClass}")
                    .OrderBy(value => value, StringComparer.Ordinal))));
        return "finding-request-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join("\u001d", values)))).ToLowerInvariant()[..16];
    }

    private static bool TryBuildUnattributableRedEscalation(
        string candidateSha,
        IReadOnlyList<FindingEvidenceArmReceipt> arms,
        FindingEvidenceBatch batch,
        string receiptId,
        out FailedGoalFindingObservation decision)
    {
        decision = FailedGoalFindingObservation.None;
        var candidate = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        if (candidate is not { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red } ||
            !string.Equals(candidate.Sha, candidateSha, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var findingIds = string.Join(',', batch.Findings.Select(finding => finding.StableId));
        if (candidate.FailingTestIdentities is not { Count: > 0 })
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
                $"Candidate RED receipt {receiptId} at candidate {candidateSha} for finding(s) {findingIds} carried no failing test identities. No worker was dispatched.");
            return true;
        }

        var baseline = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        if (baseline?.Disposition == FindingEvidenceArmDisposition.Red)
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
                $"Candidate RED receipt {receiptId} at candidate {candidateSha} for finding(s) {findingIds} is inherited from a RED Baseline arm. No worker was dispatched.");
            return true;
        }
        if (baseline?.Disposition is
                FindingEvidenceArmDisposition.ApparatusFailure or FindingEvidenceArmDisposition.Inconclusive)
        {
            decision = BuildBaselineExecutionFailureEscalation(candidateSha, batch, receiptId);
            return true;
        }

        return false;
    }

    private static FailedGoalFindingObservation BuildCappedFindingEvidenceDeliveryRetry(
        Goal goal,
        TaskSpec task,
        string candidateSha,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<string> receiptIds,
        string summary)
    {
        var findingIds = findings.Select(finding => finding.StableId).Distinct(StringComparer.Ordinal).ToArray();
        var receipts = receiptIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var evidenceIdentity =
            $"candidate_sha={candidateSha}; finding_ids={string.Join(',', findingIds)}; receipt_ids={(receipts.Length == 0 ? "none" : string.Join(',', receipts))}";
        if (findingIds.Any(findingId =>
                FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
                    goal.Timeline, task.Id, candidateSha, findingId) >= 2))
        {
            return FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingRetryCapReached,
                $"Finding-evidence delivery retry cap reached for task {task.Id}; {evidenceIdentity}. No worker was dispatched.");
        }

        return FailedGoalFindingObservation.Routed(
            FailedGoalFindingObservationKind.FindingEvidenceDeliveryRecorded,
            task.Id,
            BuildFailedGoalAttemptIdentity(task),
            $"{FindingEvidenceRetryMessagePrefix} role={task.RequiredRole}; task={task.Id.Value[..8]}; {evidenceIdentity}; {summary} " +
            "Review the finding-bound outcome in this round's context.",
            null);
    }

    private bool TryRouteReusableRedFindingEvidence(
        Goal goal,
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceRequest request,
        string executorRequest,
        string requestIdentity,
        string candidateSha,
        string executionBasisIdentity,
        ConductorAutonomyPolicy policy,
        string findingRoundFingerprint,
        out FailedGoalFindingObservation decision)
    {
        decision = FailedGoalFindingObservation.None;
        if (request.NegativeControl is not null) return false;
        if (!TryResolveReusableRedFindingEvidenceReceipt(
                requestingTask, request, candidateSha, executionBasisIdentity, out var receipt))
        {
            return false;
        }

        var group = new FindingEvidenceRequestGroup(
            requestIdentity, executorRequest, request, [finding]);
        var batch = new FindingEvidenceBatch(
            requestIdentity, executorRequest, request, [finding], [group]);
        ReattachReusableRedFindingEvidence(
            goal, requestingTask, finding, requestIdentity, receipt);
        var arms = receipt.Arms ?? [];
        var routedReceiptId = receipt.ReceiptId;
        var requestDispositions = BuildInitialRequestDispositions([batch], batch);
        var requestContext = new ConductorFocusedEvidenceRequestContext(
            findingRoundFingerprint,
            CreateFindingEvidenceBatchId(candidateSha, findingRoundFingerprint, policy.Name, batch.Identity),
            requestDispositions);
        var retainedEvidence = new FocusedEvidenceRunResult(
            executorRequest,
            receipt.Accepted,
            receipt.Passed,
            receipt.Summary,
            [],
            OutcomeReason: FindingEvidenceOutcomeReason.CandidateRed);
        if (!TryResolveMissingBaseline(
                goal, requestingTask, policy, batch, candidateSha, findingRoundFingerprint, requestContext,
                retainedEvidence, arms,
                out var evidence, out var resolvedArms, out var receiptIdentity, out decision,
                existingCandidateReceiptId: receipt.ReceiptId))
        {
            return true;
        }

        if (arms.All(arm => arm.Arm != FindingEvidenceArm.Baseline) &&
            resolvedArms.Any(arm => arm.Arm == FindingEvidenceArm.Baseline))
        {
            routedReceiptId = CreateFindingEvidenceReceiptId(
                candidateSha, findingRoundFingerprint, receiptIdentity);
            var resolvedReceipt = new FindingEvidenceReceipt(
                routedReceiptId,
                candidateSha,
                request,
                evidence.Accepted,
                evidence.IsValidEvidence,
                evidence.Summary,
                resolvedArms,
                requestDispositions,
                findingRoundFingerprint,
                executionBasisIdentity);
            _recordFindingEvidenceOutcome(
                goal.Id,
                requestingTask.Id,
                finding.StableId,
                new FindingEvidenceOutcome(
                    Honoured: true,
                    ReceiptId: routedReceiptId,
                    ResultReason: FindingEvidenceOutcomeReason.CandidateRed,
                    RequestedSelectionIdentity: requestIdentity,
                    DecisionReason: "reused-red-baseline-attribution",
                    SourceReceiptIds: [receipt.ReceiptId, routedReceiptId]),
                resolvedReceipt);
            _recordFindingEvidenceRequest(
                goal.Id,
                requestingTask.Id,
                $"finding-evidence disposition=reused-red-baseline; role={requestingTask.RequiredRole}; " +
                $"task_id={requestingTask.Id}; finding_id={finding.StableId}; candidate_sha={candidateSha}; " +
                $"source_receipt_id={receipt.ReceiptId}; receipt_id={routedReceiptId}");
            arms = resolvedArms;
        }

        var attribution = TryAttributeActionableCandidateRed(
            goal, candidateSha, arms, batch);
        if (attribution is not null)
        {
            decision = BuildActionableCandidateRedDecision(
                goal, requestingTask, candidateSha, routedReceiptId, attribution);
            return true;
        }
        if (TryBuildUnattributableRedEscalation(
                candidateSha, arms, batch, routedReceiptId, out decision))
        {
            return true;
        }

        decision = FailedGoalFindingObservation.Observed(
            FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
            $"Reusable candidate RED receipt {routedReceiptId} at candidate {candidateSha} for finding {finding.StableId} " +
            "could not be attributed without Baseline evidence. No paid worker was dispatched.");
        return true;
    }

    private static FailedGoalFindingObservation BuildActionableCandidateRedDecision(
        Goal goal,
        TaskSpec requestingTask,
        string candidateSha,
        string receiptId,
        ActionableCandidateRedAttribution attribution,
        string? additionalDetail = null)
    {
        var developer = goal.Tasks
            .TakeWhile(task => task.Id != requestingTask.Id)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Developer);
        var findingIds = string.Join(",", attribution.Findings.Select(finding => finding.StableId));
        var failingTests = string.Join(",", attribution.FailingTestIdentities);
        return developer is null
            ? FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingActionableRedRouteUnavailable,
                $"Actionable candidate RED receipt {receiptId} at candidate {candidateSha} could not be routed because no upstream Developer task exists; " +
                $"finding_ids={findingIds}; failing_tests={failingTests}. No downstream Tester or Reviewer was started.")
            : FailedGoalFindingObservation.Routed(
                FailedGoalFindingObservationKind.FindingActionableRed,
                developer.Id,
                BuildFailedGoalAttemptIdentity(developer),
                AppendActionableCandidateRedFailureDetail(
                    FormatActionableCandidateRedMessage(candidateSha, receiptId, findingIds,
                        attribution.FailingTestIdentities, additionalDetail),
                    receiptId, attribution.FailingTestIdentities, attribution.CandidateTestResultPaths),
                null);
    }

    private static string FormatActionableCandidateRedMessage(
        string candidateSha,
        string receiptId,
        string findingIds,
        IReadOnlyList<string> failingTestIdentities,
        string? additionalDetail = null) =>
        $"ACTIONABLE_CANDIDATE_RED candidate_sha={candidateSha}; receipt_id={receiptId}; finding_ids={findingIds}; " +
        $"failing_tests={string.Join(',', failingTestIdentities)}. {additionalDetail} Repair the Developer-owned source/test anchor before any remaining focused evidence or downstream verification runs.";

    private bool EveryFailingTestIsInsideCandidateChanges(
        Goal goal,
        FindingEvidenceBatch batch,
        IReadOnlyList<string> failingTestIdentities)
    {
        var changedPaths = _getLandingFileScopes(goal)
            .Select(NormalizeFindingEvidencePath)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (changedPaths.Count == 0 || failingTestIdentities.Count == 0)
        {
            return false;
        }

        var sourceRoot = string.IsNullOrWhiteSpace(_executionDirectory)
            ? Directory.GetCurrentDirectory()
            : _executionDirectory;
        var goalWorktree = GoalWorktrees.TryResolve(sourceRoot, goal.Id);
        return failingTestIdentities.All(identity =>
            batch.TypedRequest.Selections.Any(selection =>
                (goalWorktree is null
                    ? Enumerable.Empty<string>()
                    : AcceptanceTestSourceResolver.ResolveSourcePaths(goalWorktree, selection.TestProject, identity))
                    .Concat(AcceptanceTestSourceResolver.ResolveSourcePaths(sourceRoot, selection.TestProject, identity))
                    .Select(NormalizeFindingEvidencePath)
                    .Any(changedPaths.Contains)));
    }

    private static string NormalizeFindingEvidencePath(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('.', '/');

    internal ConductorParallelAcceptanceRunResult RunPreReviewBaselineArmFocusedEvidence(
        ConductorParallelAcceptanceCandidate candidate,
        string request,
        DotnetBuildEnvironmentLease? stableSlotLease,
        bool runBaselineArm,
        CancellationToken cancellationToken,
        FindingEvidenceNegativeControl? negativeControl = null) =>
        ConductorParallelAcceptanceRunResult.Focused(
            candidate,
            SelectFindingEvidenceRunner(runBaselineArm ? null : negativeControl, runBaselineArm)(
                candidate.Goal, request, stableSlotLease, cancellationToken));

    private static bool IsCandidateOnlyRed(
        string candidateSha,
        IReadOnlyList<FindingEvidenceArmReceipt> arms,
        out FindingEvidenceArmReceipt candidate)
    {
        candidate = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate)!;
        return candidate is
            {
                Accepted: true,
                Disposition: FindingEvidenceArmDisposition.Red,
                FailingTestIdentities.Count: > 0
            } &&
            string.Equals(candidate.Sha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
            arms.All(arm => arm.Arm != FindingEvidenceArm.Baseline);
    }
}
