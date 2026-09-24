using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly HashSet<string> _resolvedMissingBaselineRequests = new(StringComparer.Ordinal);

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
            var attribution = new ActionableCandidateRedAttribution(
                batch.Findings, candidateOnlyRed.FailingTestIdentities ?? []);
            decision = BuildActionableCandidateRedDecision(
                goal, requestingTask, candidateSha, candidateReceiptId, attribution,
                "Baseline arm was previously absent; the finding-bound outcome is already recorded.");
            return false;
        }

        var baselineContext = requestContext with
        {
            BatchId = requestContext.BatchId + "-baseline-arm",
            RunBaselineArm = true
        };
        if (!TryReconcileFocusedEvidenceAttempt(
                goal, policy, batch.Request, candidateSha, "finding-baseline-arm", baselineContext,
                out var baselineEvidence, out var baselineAttempt, out decision, out var attemptKind))
        {
            if (attemptKind is null or ConductorParallelAcceptanceAttemptDecisionKind.Started or
                    ConductorParallelAcceptanceAttemptDecisionKind.Running || baselineAttempt is null ||
                baselineAttempt.Outcome is not (
                    ConductorParallelAcceptanceAttemptOutcome.ProcessDied or
                    ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts or
                    ConductorParallelAcceptanceAttemptOutcome.LaunchFailed or
                    ConductorParallelAcceptanceAttemptOutcome.Faulted or
                    ConductorParallelAcceptanceAttemptOutcome.GateEngineFault))
            {
                return false;
            }
            return RecordMissingBaselineAndRoute(
                goal, requestingTask, batch, candidateSha, findingRoundFingerprint, requestIdentity,
                candidateReceiptId, initialEvidence, initialArms, baselineAttempt.ResultPath,
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
                goal, requestingTask, batch, candidateSha, findingRoundFingerprint, requestIdentity,
                candidateReceiptId, initialEvidence, initialArms, baselineAttempt?.ResultPath,
                reason, out decision);
        }

        evidence = baselineEvidence;
        arms = baselineArms;
        return true;
    }

    private bool RecordMissingBaselineAndRoute(
        Goal goal,
        TaskSpec requestingTask,
        FindingEvidenceBatch batch,
        string candidateSha,
        string findingRoundFingerprint,
        string requestIdentity,
        string receiptId,
        FocusedEvidenceRunResult candidateEvidence,
        IReadOnlyList<FindingEvidenceArmReceipt> candidateArms,
        string? resultPath,
        string reason,
        out FailedGoalFindingObservation decision)
    {
        var guard = $"candidate_sha={candidateSha}; finding_round={findingRoundFingerprint}; request_identity={requestIdentity}";
        var receipt = new FindingEvidenceReceipt(
            receiptId, candidateSha, batch.TypedRequest, candidateEvidence.Accepted,
            candidateEvidence.IsValidEvidence, candidateEvidence.Summary, candidateArms,
            FindingRoundFingerprint: findingRoundFingerprint);
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
        var candidate = candidateArms.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var attribution = new ActionableCandidateRedAttribution(
            batch.Findings, candidate.FailingTestIdentities ?? []);
        decision = BuildActionableCandidateRedDecision(
            goal, requestingTask, candidateSha, receiptId, attribution,
            $"Baseline arm absent: {reason}; result_path={resultPath ?? "none"}; candidate_summary={candidate.Summary}");
        return false;
    }

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
                "Candidate RED was routed to the Developer; no operator action is required.",
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
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
                $"Baseline execution failure for candidate RED finding(s) {findingIds} at candidate {candidateSha}; " +
                $"candidate_receipt_id={receiptId}; baseline_receipt_id={receiptId}. No worker was dispatched.");
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
        var failingTests = string.Join(",", attribution.FailingTestIdentities);
        var findingIds = string.Join(",", attribution.Findings.Select(finding => finding.StableId));
        return developer is null
            ? FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingActionableRedRouteUnavailable,
                $"Actionable candidate RED receipt {receiptId} at candidate {candidateSha} could not be routed because no upstream Developer task exists; " +
                $"finding_ids={findingIds}; failing_tests={failingTests}. No downstream Tester or Reviewer was started.")
            : FailedGoalFindingObservation.Routed(
                FailedGoalFindingObservationKind.FindingActionableRed,
                developer.Id,
                BuildFailedGoalAttemptIdentity(developer),
                $"ACTIONABLE_CANDIDATE_RED candidate_sha={candidateSha}; receipt_id={receiptId}; finding_ids={findingIds}; " +
                $"failing_tests={failingTests}. {additionalDetail} Repair the Developer-owned source/test anchor before any remaining focused evidence or downstream verification runs.",
                null);
    }

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
        CancellationToken cancellationToken) =>
        ConductorParallelAcceptanceRunResult.Focused(
            candidate,
            (runBaselineArm ? _runDualArmFocusedEvidence : _runFocusedEvidence)(
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
