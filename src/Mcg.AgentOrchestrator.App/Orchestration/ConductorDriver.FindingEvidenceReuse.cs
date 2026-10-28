using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private VerifyingFindingTrigger? BuildTesterDeveloperOwnedFindingTrigger(Goal goal, TaskSpec task)
    {
        if (task is not { Status: WorkTaskStatus.Failed, RequiredRole: AgentRole.Tester } ||
            task.LastVerification?.MergedReviewFindings is not { } mergedFindings ||
            !WorkerResultBlockers.AssessSubscriptionVerification(task, task.LastVerification).AllowsCompletion)
        {
            return null;
        }

        var developerOwnedFindings = ReviewFindingRouting.Project(
                ReviewFindings.GetOpenBlockingFindings(
                    mergedFindings,
                    goal.EffectiveAcceptanceCriteriaCorrections))
            .Where(projection =>
                projection.TargetRole == AgentRole.Developer &&
                projection.Finding.Category is
                    FindingCategory.Correctness or
                    FindingCategory.TestCoverage or
                    FindingCategory.SpecCompliance or
                    FindingCategory.CodeQuality)
            .Select(projection => projection.Finding)
            .ToArray();
        if (developerOwnedFindings.Length == 0)
        {
            return null;
        }

        var upstreamDeveloper = TesterFindingDeveloperTargetPolicy.Select(goal, task, _executionDirectory);
        var finding = string.Join(
            "; ",
            developerOwnedFindings.Select(item =>
                $"stable_id={item.StableId} category={item.Category} description={item.Description}"));
        return new VerifyingFindingTrigger(
            task,
            finding,
            [],
            upstreamDeveloper,
            RequiresCommittedTarget: true,
            DeveloperOwnedFindings: developerOwnedFindings);
    }

    /// <summary>
    /// A Tester round whose Developer-owned findings are ALL still waiting on the focused execution
    /// the conductor itself scheduled is pending verification, not a demonstrated writable defect.
    /// Run that scheduled execution once for the exact candidate before spending a paid Developer
    /// round on a candidate nobody has measured. A single writable finding in the round - no typed
    /// request, a receipt already taken at this candidate, a permanent refusal, or an unknown
    /// candidate - keeps today's immediate Developer dispatch with zero focused runs.
    /// </summary>
    private bool TryRouteTesterFindingToPendingEvidence(
        Goal goal,
        ConductorAutonomyPolicy policy,
        VerifyingFindingTrigger trigger,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        if (!_focusedEvidenceRunnerConfigured ||
            trigger.DeveloperOwnedFindings is not { Count: > 0 } developerOwnedFindings ||
            // No feasible upstream Developer: today's escalation is the correct unchanged outcome.
            trigger.TargetTask is not { } upstreamDeveloper ||
            // An un-consumed Developer retry is already going to change the candidate, so evidence
            // taken now would be spent on a superseded candidate.
            HasUnconsumedRetry(upstreamDeveloper))
        {
            return false;
        }

        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!ConductorGitRevisionReader.IsValid(candidateSha) ||
            !FindingEvidenceExecutionClassifier.IsPendingExecutionOnly(
                developerOwnedFindings, trigger.TriggeringTask, candidateSha))
        {
            return false;
        }

        if (!TryBuildFindingEvidenceRequest(goal, trigger.TriggeringTask, policy, out observation))
        {
            // The evidence path declined to act; fall through to the unchanged Developer dispatch.
            observation = FailedGoalFindingObservation.None;
            return false;
        }

        if (!WorkerResultBlockers.TryFindReviewFindingRound(
                trigger.TriggeringTask.LastVerification, out var round, out _))
        {
            throw new InvalidOperationException("Pending finding evidence has no review finding round.");
        }

        var findingRound = BuildFindingRoundFingerprint(trigger.TriggeringTask, round);
        var requestIdentity = BuildFindingEvidenceRequestIdentity(developerOwnedFindings);
        var guard = $"candidate_sha={candidateSha}; finding_round={findingRound}; request_identity={requestIdentity}";
        if (!goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                evt.TaskId == trigger.TriggeringTask.Id &&
                evt.Message.Contains("disposition=pending-execution-gate;", StringComparison.Ordinal) &&
                evt.Message.Contains(guard, StringComparison.Ordinal)))
        {
            _recordFindingEvidenceRequest(
                goal.Id,
                trigger.TriggeringTask.Id,
                $"finding-evidence disposition=pending-execution-gate; role={trigger.TriggeringTask.RequiredRole}; " +
                $"task_id={trigger.TriggeringTask.Id}; " +
                $"finding_ids={string.Join(",", developerOwnedFindings.Select(item => item.StableId))}; " +
                $"{guard}; deferred_developer_task_id={upstreamDeveloper.Id}");
        }
        return true;
    }

    internal static bool HasUnconsumedRetry(TaskSpec task) =>
        task.LatestRetryAt is not null &&
        task.Status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned &&
        task.LastDispatch is null &&
        task.LastVerification is null;

    private bool TryDeliverGreenTesterFindingEvidence(
        Goal goal,
        ConductorAutonomyPolicy policy,
        VerifyingFindingTrigger trigger,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        if (!_focusedEvidenceRunnerConfigured ||
            trigger.DeveloperOwnedFindings is not { Count: > 0 } findings ||
            trigger.TargetTask is not { } upstreamDeveloper ||
            HasUnconsumedRetry(upstreamDeveloper))
        {
            return false;
        }

        var task = trigger.TriggeringTask;
        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!ConductorGitRevisionReader.IsValid(candidateSha)) return false;
        var executionBasisIdentity = BuildFindingEvidenceExecutionBasisIdentity(
            _getFindingEvidenceEngineSettings(goal));

        var currentReceipts = new List<string>();
        foreach (var finding in findings)
        {
            var state = FindingEvidenceExecutionClassifier.Classify(task, finding, candidateSha);
            if (state == FindingEvidenceExecutionState.PendingExecution) continue;
            if (state != FindingEvidenceExecutionState.ExecutedOnCandidate ||
                finding.EvidenceOutcome is not
                {
                    Honoured: true,
                    ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
                    ReceiptId: { Length: > 0 } receiptId
                } ||
                !task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
                    .Any(receipt =>
                        string.Equals(receipt.ReceiptId, receiptId, StringComparison.Ordinal) &&
                        string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                        IsReusableGreenFindingEvidenceReceipt(
                            receipt, candidateSha!, executionBasisIdentity)))
            {
                return false;
            }
            currentReceipts.Add(receiptId);
        }

        if (currentReceipts.Count == 0) return false;
        if (TryBuildFindingEvidenceRequest(goal, task, policy, out observation)) return true;
        if (currentReceipts.Count != findings.Count) return false;

        observation = BuildReceiptClosureOrDeliveryRetry(
            goal, task, candidateSha!, findings, currentReceipts,
            "The Developer-owned findings have honoured GREEN evidence at this candidate.");
        return true;
    }

    private static bool HasCurrentFindingEvidenceReceipt(
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceRequest request,
        string candidateSha,
        string findingRoundFingerprint,
        string executionBasisIdentity)
    {
        var receiptId = finding.EvidenceOutcome?.ReceiptId;
        if (string.IsNullOrWhiteSpace(receiptId) || candidateSha == "unavailable")
        {
            return false;
        }

        var identity = BuildFindingEvidenceIdentity(request);
        if (!TryGetFindingEvidenceCoverage(request, out var requestedCoverage))
        {
            return false;
        }

        return requestingTask.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .Any(receipt =>
                string.Equals(receipt.ReceiptId, receiptId, StringComparison.Ordinal) &&
                string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    receipt.FindingRoundFingerprint,
                    findingRoundFingerprint,
                    StringComparison.Ordinal) &&
                IsReusableGreenFindingEvidenceReceipt(receipt, candidateSha, executionBasisIdentity) &&
                TryGetFindingEvidenceCoverage(FindingEvidenceExecutedCoverage.ExecutedRequest(receipt, FormatFindingEvidenceSelection), out var executedCoverage) &&
                requestedCoverage.SetEquals(executedCoverage) &&
                (string.Equals(BuildFindingEvidenceIdentity(receipt.Request), identity, StringComparison.Ordinal) ||
                    (receipt.RequestDispositions ?? []).Any(disposition =>
                        string.Equals(disposition.FindingStableId, finding.StableId, StringComparison.Ordinal) &&
                        string.Equals(disposition.RequestIdentity, identity, StringComparison.Ordinal))));
    }

    private void ReattachReusableGreenFindingEvidence(
        Goal goal,
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceOutcome outcome,
        FindingEvidenceReceipt receipt)
    {
        _recordFindingEvidenceOutcome(
            goal.Id, requestingTask.Id, finding.StableId, outcome, receipt);
        _recordFindingEvidenceRequest(
            goal.Id,
            requestingTask.Id,
            $"finding-evidence disposition=reused-green; role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
            $"finding_id={finding.StableId}; candidate_sha={receipt.CandidateSha}; receipt_id={receipt.ReceiptId}");
    }

    private void ReattachCoverageReusableGreenFindingEvidence(
        Goal goal,
        TaskSpec requestingTask,
        ReviewFinding finding,
        IReadOnlyList<FindingEvidenceReceipt> receipts,
        string requestedIdentity,
        string coverageReason)
    {
        if (receipts.Count == 0)
        {
            throw new InvalidOperationException("Coverage reuse requires at least one authoritative source receipt.");
        }

        var sourceReceiptIds = receipts.Select(receipt => receipt.ReceiptId).ToArray();
        var primaryReceipt = receipts[^1];
        var outcome = new FindingEvidenceOutcome(
            Honoured: true,
            ReceiptId: primaryReceipt.ReceiptId,
            ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
            RequestedSelectionIdentity: requestedIdentity,
            DecisionReason: receipts.All(source => source.FindingRoundFingerprint == "pre-tester-deferred")
                ? "pre-tester-covered" : $"reused-covered-green:{coverageReason}",
            SourceReceiptIds: sourceReceiptIds);
        foreach (var receipt in receipts)
        {
            _recordFindingEvidenceOutcome(
                goal.Id, requestingTask.Id, finding.StableId, outcome, receipt);
            var sourceFindingId = (receipt.RequestDispositions ?? [])
                .LastOrDefault(existing => existing.Disposition.StartsWith("executed-", StringComparison.Ordinal))
                ?.FindingStableId ?? "unavailable";
            _recordFindingEvidenceRequest(
                goal.Id,
                requestingTask.Id,
                $"finding-evidence disposition=reused-covered-green; route=coverage; role={requestingTask.RequiredRole}; " +
                $"task_id={requestingTask.Id}; finding_id={finding.StableId}; requested_identity={requestedIdentity}; " +
                $"executed_identity={BuildFindingEvidenceIdentity(receipt.Request)}; source_receipt_id={receipt.ReceiptId}; " +
                $"source_finding_id={sourceFindingId}; coverage={coverageReason}; candidate_sha={receipt.CandidateSha}");
        }
    }

    private static bool TryResolveReusableRedFindingEvidenceReceipt(
        TaskSpec requestingTask,
        FindingEvidenceRequest normalizedRequest,
        string candidateSha,
        string executionBasisIdentity,
        out FindingEvidenceReceipt receipt)
    {
        receipt = null!;
        if (candidateSha == "unavailable" ||
            string.IsNullOrWhiteSpace(executionBasisIdentity) ||
            !TryGetFindingEvidenceCoverage(normalizedRequest, out var requestedCoverage))
        {
            return false;
        }

        foreach (var candidateReceipt in requestingTask.VerificationHistory
                     .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
                     .Reverse())
        {
            var candidateArm = candidateReceipt.Arms?
                .SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
            if (candidateReceipt is not { Accepted: true, Passed: false } ||
                !string.Equals(candidateReceipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidateReceipt.ExecutionBasisIdentity, executionBasisIdentity, StringComparison.Ordinal) ||
                candidateReceipt.Request.NegativeControl != normalizedRequest.NegativeControl ||
                !FindingEvidenceRevertPaths.SamePaths(candidateReceipt.Request.RevertPaths, normalizedRequest.RevertPaths) ||
                !FindingEvidenceMutation.Same(candidateReceipt.Request.Mutation, normalizedRequest.Mutation) ||
                candidateArm is not
                {
                    Accepted: true,
                    Disposition: FindingEvidenceArmDisposition.Red,
                    ExecutedTestCount: > 0,
                    FailingTestIdentities.Count: > 0
                } ||
                (candidateReceipt.Arms ?? []).Any(arm => arm.Disposition is
                    FindingEvidenceArmDisposition.ApparatusFailure or FindingEvidenceArmDisposition.Inconclusive) ||
                !TryGetFindingEvidenceCoverage(FindingEvidenceExecutedCoverage.ExecutedRequest(candidateReceipt, FormatFindingEvidenceSelection), out var executedCoverage) ||
                !requestedCoverage.SetEquals(executedCoverage) ||
                !FindingEvidenceTrxCoverage.HasContentBoundRedTrxCoverage(
                    candidateArm,
                    ResolveFindingEvidenceRequiredTestClasses(candidateReceipt.Request)))
            {
                continue;
            }

            receipt = candidateReceipt;
            return true;
        }

        return false;
    }

    private void ReattachReusableRedFindingEvidence(
        Goal goal,
        TaskSpec requestingTask,
        ReviewFinding finding,
        string requestIdentity,
        FindingEvidenceReceipt receipt)
    {
        var outcome = new FindingEvidenceOutcome(
            Honoured: true,
            ReceiptId: receipt.ReceiptId,
            ResultReason: FindingEvidenceOutcomeReason.CandidateRed,
            RequestedSelectionIdentity: requestIdentity,
            DecisionReason: "reused-red",
            SourceReceiptIds: [receipt.ReceiptId]);
        _recordFindingEvidenceOutcome(goal.Id, requestingTask.Id, finding.StableId, outcome, receipt);
        _recordFindingEvidenceRequest(
            goal.Id,
            requestingTask.Id,
            $"finding-evidence disposition=reused-red; role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
            $"finding_id={finding.StableId}; candidate_sha={receipt.CandidateSha}; receipt_id={receipt.ReceiptId}");
    }

    internal static bool TryResolveFindingEvidenceCoverage(
        TaskSpec requestingTask,
        FindingEvidenceRequest normalizedRequest,
        string candidateSha,
        string executionBasisIdentity,
        out IReadOnlyList<FindingEvidenceReceipt> reusableReceipts,
        out IReadOnlyList<FindingEvidenceSelection> uncoveredSelections,
        out string coverageReason,
        IReadOnlyList<PreTesterCoverageComposition.PreTesterCoverageSource>? preTesterCoverage = null)
    {
        reusableReceipts = [];
        uncoveredSelections = normalizedRequest.Selections;
        coverageReason = string.Empty;
        if (normalizedRequest.NegativeControl is not null || normalizedRequest.RevertPaths is not null ||
            normalizedRequest.Mutation is not null) preTesterCoverage = [];
        if (candidateSha == "unavailable" ||
            string.IsNullOrWhiteSpace(executionBasisIdentity) ||
            !TryGetFindingEvidenceCoverage(normalizedRequest, out var requestedCoverage))
        {
            return false;
        }

        var validOutcomesByReceiptId = requestingTask.VerificationHistory
            .SelectMany(verification => verification.MergedReviewFindings ?? [])
            .Select(finding => finding.EvidenceOutcome)
            .OfType<FindingEvidenceOutcome>()
            .Where(outcome =>
                outcome is { Honoured: true, ReceiptId.Length: > 0, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence } ||
                outcome is { Honoured: true, ReceiptId.Length: > 0, ResultReason: FindingEvidenceOutcomeReason.Unknown,
                    DecisionReason: "reused-pre-tester-invalidated:pending-execution" })
            .GroupBy(outcome => outcome.ReceiptId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var latestReceiptBySelection = new Dictionary<string, FindingEvidenceReceipt>(StringComparer.Ordinal);
        var latestTimeBySelection = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var preTesterIds = (preTesterCoverage ?? []).Select(source => source.ReceiptId).ToHashSet(StringComparer.Ordinal);
        foreach (var source in preTesterCoverage ?? [])
        {
            var executedCoverage = source.Receipt is { } receipt
                ? FindingEvidenceExecutedCoverage.ExecutedSelections(receipt, FormatFindingEvidenceSelection)
                    .Select(FormatFindingEvidenceSelection).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var selection in source.Selections.Select(FormatFindingEvidenceSelection).Where(requestedCoverage.Contains))
            {
                latestTimeBySelection[selection] = source.CompletedAt;
                latestReceiptBySelection.Remove(selection);
                // Preserve the latest attempt's veto without treating an omitted class as executed.
                if (executedCoverage.Contains(selection)) latestReceiptBySelection[selection] = source.Receipt!;
            }
        }
        foreach (var verification in requestingTask.VerificationHistory)
        {
            if (!WorkerResultBlockers.TryFindReviewFindingRound(verification, out var receiptRound, out _))
            {
                continue;
            }

            var executedRoundFingerprint = BuildFindingRoundFingerprint(
                requestingTask.Id,
                verification.CompletedAt,
                receiptRound);
            foreach (var receipt in verification.FindingEvidenceReceipts ?? [])
            {
                // Copies attached for delivery are references, not new executions. Their original
                // authority and chronology are supplied by the pre-Tester reader above.
                if (preTesterIds.Contains(receipt.ReceiptId)) continue;
                if (!string.Equals(receipt.FindingRoundFingerprint, executedRoundFingerprint, StringComparison.Ordinal) ||
                    !string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(receipt.ExecutionBasisIdentity, executionBasisIdentity, StringComparison.Ordinal) ||
                    receipt.Request.NegativeControl != normalizedRequest.NegativeControl ||
                    !FindingEvidenceRevertPaths.SamePaths(receipt.Request.RevertPaths, normalizedRequest.RevertPaths) ||
                    !FindingEvidenceMutation.Same(receipt.Request.Mutation, normalizedRequest.Mutation) ||
                    !TryGetFindingEvidenceCoverage(FindingEvidenceExecutedCoverage.ExecutedRequest(receipt, FormatFindingEvidenceSelection), out var receiptCoverage) ||
                    !receiptCoverage.Overlaps(requestedCoverage))
                {
                    continue;
                }

                // Preserve main's authority rule: the newest receipt which executed a selection
                // wins even when it is RED, incomplete, rejected, or otherwise non-reusable.
                foreach (var selection in receiptCoverage.Where(requestedCoverage.Contains))
                {
                    if (latestTimeBySelection.TryGetValue(selection, out var time) && time > verification.CompletedAt) continue;
                    latestReceiptBySelection[selection] = receipt;
                    latestTimeBySelection[selection] = verification.CompletedAt;
                }
            }
        }

        var reusableBySelection = new Dictionary<string, FindingEvidenceReceipt>(StringComparer.Ordinal);
        foreach (var selection in requestedCoverage)
        {
            if (latestReceiptBySelection.TryGetValue(selection, out var receipt) &&
                (validOutcomesByReceiptId.ContainsKey(receipt.ReceiptId) ||
                 (preTesterCoverage ?? []).Any(source => source.Receipt == receipt)) &&
                IsReusableGreenFindingEvidenceReceipt(receipt, candidateSha, executionBasisIdentity))
            {
                reusableBySelection[selection] = receipt;
            }
        }

        var expandedSelections = FindingEvidenceRoundPlanner.ExpandSelections(normalizedRequest.Selections);
        uncoveredSelections = expandedSelections
            .Where(selection => !reusableBySelection.ContainsKey(FormatFindingEvidenceSelection(selection)))
            .ToArray();
        reusableReceipts = expandedSelections
            .Select(selection => reusableBySelection.GetValueOrDefault(FormatFindingEvidenceSelection(selection)))
            .Where(receipt => receipt is not null)
            .Cast<FindingEvidenceReceipt>()
            .DistinctBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .ToArray();
        if (reusableReceipts.Count == 0)
        {
            return false;
        }

        if (uncoveredSelections.Count > 0)
        {
            coverageReason = "partial";
        }
        else if (reusableReceipts.Count > 1)
        {
            coverageReason = "composite";
        }
        else
        {
            _ = TryGetFindingEvidenceCoverage(FindingEvidenceExecutedCoverage.ExecutedRequest(reusableReceipts[0], FormatFindingEvidenceSelection), out var sourceCoverage);
            coverageReason = requestedCoverage.SetEquals(sourceCoverage) ? "exact" : "subset";
        }

        return true;
    }

    internal static bool IsReusableGreenFindingEvidenceReceipt(
        FindingEvidenceReceipt receipt,
        string candidateSha,
        string executionBasisIdentity)
    {
        var executedRequest = FindingEvidenceExecutedCoverage.ExecutedRequest(receipt, FormatFindingEvidenceSelection);
        if (!TryGetFindingEvidenceCoverage(executedRequest, out var receiptCoverage))
        {
            return false;
        }

        var dispositions = receipt.RequestDispositions ?? [];
        var hasCandidateOmissions = (receipt.Arms ?? []).Any(arm =>
            arm.Arm == FindingEvidenceArm.Candidate && arm.NotRunSelections.Count > 0);
        // An incomplete request can still contribute its actually executed classes.
        // ExecutedRequest excludes omissions; retained GREEN TRX evidence below proves the remainder.
        var coverageDispositions = dispositions
            .Where(disposition =>
                disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal) ||
                (hasCandidateOmissions && disposition is
                    { Disposition: "incomplete", Reason: "candidate-selections-not-executed", Decision: FindingEvidenceRequestDecision.PendingExecution } &&
                 disposition.SourceReceiptIds?.Contains(receipt.ReceiptId, StringComparer.Ordinal) == true))
            .ToArray();
        var executedDispositionCoverage = coverageDispositions.SelectMany(disposition =>
                FindingEvidenceRoundPlanner.ExpandSelections(ParseDispositionSelections(disposition.RequestIdentity)))
            .Select(FormatFindingEvidenceSelection).ToHashSet(StringComparer.Ordinal);
        if (receipt is not { Accepted: true, Passed: true } ||
            !string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.ExecutionBasisIdentity, executionBasisIdentity, StringComparison.Ordinal) ||
            coverageDispositions.Length == 0 ||
            !executedDispositionCoverage.IsSupersetOf(receiptCoverage) ||
            dispositions.Any(disposition =>
                coverageDispositions.Any(executed =>
                    string.Equals(executed.RequestIdentity, disposition.RequestIdentity, StringComparison.Ordinal)) &&
                !coverageDispositions.Contains(disposition)))
        {
            return false;
        }

        var candidateArm = (receipt.Arms ?? []).FirstOrDefault(arm =>
            arm is
            {
                Arm: FindingEvidenceArm.Candidate,
                Disposition: FindingEvidenceArmDisposition.Green,
                Accepted: true,
                Passed: true,
                ExecutedTestCount: > 0
            } &&
            string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase));
        return candidateArm is not null &&
            AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(
                candidateArm.TestResultPaths,
                candidateArm.ReceiptArtifacts,
                candidateArm.ExecutedTestCount!.Value,
                ResolveFindingEvidenceRequiredTestClasses(executedRequest));
    }

    private static IReadOnlyList<string> ResolveFindingEvidenceRequiredTestClasses(FindingEvidenceRequest request)
    {
        var required = new List<string>();
        foreach (var selection in FindingEvidenceRoundPlanner.ExpandSelections(request.Selections))
        {
            if (!FindingEvidenceFilterSyntax.TryGetExpandableClass(selection.TestClass, out var testClass, out _))
            {
                return [];
            }

            required.Add(testClass);
        }

        return required.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static FindingEvidenceArmReceipt CreateFindingEvidenceArmReceipt(FocusedEvidenceArmRunResult arm)
    {
        var testResultPaths = NormalizeFindingEvidenceTestResultPaths(
            arm.Checks.SelectMany(check => check.TestResultPaths ?? []));
        var receiptArtifacts = CaptureFindingEvidenceTrxArtifacts(testResultPaths);
        return new FindingEvidenceArmReceipt(
            arm.Arm,
            arm.Sha,
            arm.Disposition,
            arm.Accepted,
            arm.Passed,
            arm.Summary,
            testResultPaths
                .Concat(arm.Checks.Select(check => check.ArtifactsPath ?? string.Empty))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            arm.Checks
                .SelectMany(check => check.FailingTestIdentities ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            TryGetExecutedTestCount(arm.Checks),
            testResultPaths,
            receiptArtifacts, arm.RestoredPaths, arm.DroppedPaths, arm.NotRunSelections);
    }

    private static IReadOnlyList<string> NormalizeFindingEvidenceTestResultPaths(IEnumerable<string> paths)
    {
        try
        {
            var materialized = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            if (materialized.Any(path => !Path.IsPathFullyQualified(path)))
            {
                return [];
            }

            return materialized
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }
    }

    private static IReadOnlyList<AcceptanceCohortEvidenceArtifact> CaptureFindingEvidenceTrxArtifacts(
        IReadOnlyList<string> testResultPaths)
    {
        var artifacts = new List<AcceptanceCohortEvidenceArtifact>();
        try
        {
            foreach (var path in testResultPaths)
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                artifacts.Add(new AcceptanceCohortEvidenceArtifact(
                    "trx",
                    path,
                    Convert.ToHexStringLower(SHA256.HashData(stream)),
                    stream.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }

        return artifacts;
    }

    internal static string BuildFindingEvidenceExecutionBasisIdentity(AcceptanceGateEngineSettings settings)
    {
        var invocations = settings.MtpInvocations
            .OrderBy(invocation => invocation.Project, StringComparer.OrdinalIgnoreCase)
            .Select(invocation => string.Join(
                "\u001f",
                invocation.Project.Replace('\\', '/'),
                invocation.ExecutablePathTemplate.Replace('\\', '/'),
                string.Join("\u001e", invocation.Arguments)));
        var payload = string.Join(
            "\u001d",
            "finding-focused-evidence-v1",
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.ProcessArchitecture,
            settings.EnforceStructuralCoverage,
            string.Join("\u001c", invocations));
        return "focused-v1-sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    private static int? TryGetExecutedTestCount(IReadOnlyList<AcceptanceCheckResult> checks) =>
        checks.Count > 0 && checks.All(check => check.ExecutedTestCount.HasValue)
            ? checks.Sum(check => check.ExecutedTestCount!.Value)
            : null;

    internal static string BuildFindingRoundFingerprint(TaskSpec requestingTask, ReviewFindingRound round)
        => BuildFindingRoundFingerprint(
            requestingTask.Id,
            requestingTask.LastVerification?.CompletedAt,
            round);

    internal static string BuildFindingRoundFingerprint(
        TaskId requestingTaskId,
        DateTimeOffset? verificationCompletedAt,
        ReviewFindingRound round)
    {
        var verificationTicks = verificationCompletedAt?.ToUniversalTime().Ticks ?? 0;
        var findings = round.Findings
            .OrderBy(finding => finding.StableId, StringComparer.Ordinal)
            .Select(finding => string.Join(
                "\u001f",
                finding.StableId,
                finding.State,
                finding.Severity,
                finding.Category,
                finding.Location.File,
                finding.Location.Region,
                finding.Description,
                string.Join(
                    "\u001e",
                    (finding.EvidenceRequest?.Selections ?? [])
                        .OrderBy(selection => selection.TestProject, StringComparer.Ordinal)
                        .ThenBy(selection => selection.TestClass, StringComparer.Ordinal)
                        .Select(selection => $"{selection.TestProject}:{selection.TestClass}"))));
        var payload = $"{requestingTaskId.Value}\u001d{verificationTicks}\u001d{string.Join("\u001d", findings)}";
        return "finding-round-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant()[..24];
    }

    private static bool TryGetFindingEvidenceCoverage(
        FindingEvidenceRequest request,
        out HashSet<string> coverage)
    {
        coverage = FindingEvidenceRoundPlanner.ExpandSelections(request.Selections)
            .Where(selection =>
                !string.IsNullOrWhiteSpace(selection.TestProject) &&
                !string.IsNullOrWhiteSpace(selection.TestClass))
            .Select(FormatFindingEvidenceSelection)
            .ToHashSet(StringComparer.Ordinal);
        return coverage.Count > 0;
    }

    internal static IReadOnlyList<FindingEvidenceSelection> ParseDispositionSelections(string identity)
    {
        // Request identities use project:selection alternatives. A bare alternative inherits
        // its preceding project, which also supports older positive-disjunction receipts.
        // Split identities only outside selector groups, then expand complete expressions.
        // Splitting every pipe first loses the balanced parentheses needed by class parsing.
        var tokens = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < identity.Length; index++)
        {
            if (identity[index] == '(') depth++;
            else if (identity[index] == ')' && --depth < 0) return [];
            else if (identity[index] == '|' && depth == 0)
            {
                tokens.Add(identity[start..index].Trim());
                start = index + 1;
            }
        }
        if (depth != 0) return [];
        tokens.Add(identity[start..].Trim());

        var selections = new List<FindingEvidenceSelection>();
        string? project = null;
        foreach (var token in tokens.Where(token => token.Length > 0))
        {
            var separator = token.IndexOf(':');
            if (separator >= 0)
            {
                project = token[..separator];
                selections.Add(new(project, token[(separator + 1)..]));
            }
            else if (project is not null && FindingEvidenceRoundPlanner.TryExpandClasses(token, out _))
                selections.Add(new(project, token));
        }
        return selections;
    }

    // One refusal truth: routing and the worker-visible execution state read the same rule.
    private static bool IsPermanentFindingEvidenceRefusal(FindingEvidenceOutcome outcome) =>
        FindingEvidenceExecutionClassifier.IsPermanentRefusal(outcome);
}
