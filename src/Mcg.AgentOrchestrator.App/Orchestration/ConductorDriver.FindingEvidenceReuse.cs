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
            task.LastVerification?.MergedReviewFindings is not { } mergedFindings)
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

        var upstreamDeveloper = goal.Tasks
            .TakeWhile(candidate => candidate.Id != task.Id)
            .LastOrDefault(candidate =>
                candidate.RequiredRole == AgentRole.Developer &&
                HasEverCommittedOutput(candidate));
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

    private static bool HasUnconsumedRetry(TaskSpec task) =>
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

    private static IReadOnlyList<FindingEvidenceBatch> ExcludePassedRoundFindingEvidenceBatches(
        TaskSpec requestingTask,
        IReadOnlyList<FindingEvidenceBatch> batches,
        string candidateSha,
        string findingRoundFingerprint)
    {
        if (candidateSha == "unavailable")
        {
            return batches;
        }

        // Completing a batch in this round does not require reattaching content-bound
        // evidence. Cross-round reuse keeps its stricter artifact and coverage checks.
        var passedReceiptIds = requestingTask.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .Where(receipt => receipt is { Accepted: true, Passed: true } &&
                string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(receipt.FindingRoundFingerprint, findingRoundFingerprint, StringComparison.Ordinal))
            .Select(receipt => receipt.ReceiptId)
            .ToHashSet(StringComparer.Ordinal);

        return batches.Where(batch => !passedReceiptIds.Contains(
                CreateFindingEvidenceReceiptId(candidateSha, findingRoundFingerprint, batch.Identity)))
            .ToArray();
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
                TryGetFindingEvidenceCoverage(receipt.Request, out var executedCoverage) &&
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
            DecisionReason: $"reused-covered-green:{coverageReason}",
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
                candidateArm is not
                {
                    Accepted: true,
                    Disposition: FindingEvidenceArmDisposition.Red,
                    ExecutedTestCount: > 0,
                    FailingTestIdentities.Count: > 0
                } ||
                (candidateReceipt.Arms ?? []).Any(arm => arm.Disposition is
                    FindingEvidenceArmDisposition.ApparatusFailure or FindingEvidenceArmDisposition.Inconclusive) ||
                !TryGetFindingEvidenceCoverage(candidateReceipt.Request, out var executedCoverage) ||
                !requestedCoverage.SetEquals(executedCoverage) ||
                !HasContentBoundRedTrxCoverage(
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

    private static bool HasContentBoundRedTrxCoverage(
        FindingEvidenceArmReceipt candidateArm,
        IReadOnlyCollection<string> requiredTestClasses)
    {
        if (candidateArm is not { ExecutedTestCount: > 0 } ||
            candidateArm.TestResultPaths is not { Count: > 0 } testResultPaths ||
            candidateArm.ReceiptArtifacts is not { Count: > 0 } receiptArtifacts ||
            requiredTestClasses.Count == 0 ||
            !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(testResultPaths))
        {
            return false;
        }

        try
        {
            var normalizedPaths = testResultPaths
                .Select(Path.GetFullPath)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var trxArtifacts = receiptArtifacts
                .Where(artifact => artifact.Kind.Equals("trx", StringComparison.Ordinal))
                .OrderBy(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (trxArtifacts.Length != normalizedPaths.Length ||
                !trxArtifacts.Select(artifact => artifact.Path)
                    .SequenceEqual(normalizedPaths, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var executedCount = 0;
            var failedCount = 0;
            var executedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            foreach (var artifact in trxArtifacts)
            {
                using var stream = File.Open(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != artifact.Length || artifact.Length <= 0)
                {
                    return false;
                }

                var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (!hash.Equals(artifact.Sha256, StringComparison.Ordinal))
                {
                    return false;
                }

                stream.Position = 0;
                var document = XDocument.Load(stream, LoadOptions.None);
                var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var test in document.Root?
                             .Element(trx + "TestDefinitions")?
                             .Elements(trx + "UnitTest") ?? [])
                {
                    var id = (string?)test.Attribute("id");
                    var className = (string?)test.Element(trx + "TestMethod")?.Attribute("className");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(className) ||
                        !definitions.TryAdd(id, className))
                    {
                        return false;
                    }
                }

                foreach (var result in document.Root?
                             .Element(trx + "Results")?
                             .Elements(trx + "UnitTestResult") ?? [])
                {
                    var outcome = (string?)result.Attribute("outcome");
                    if (!string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var id = (string?)result.Attribute("testId");
                    if (string.IsNullOrWhiteSpace(id) || !definitions.TryGetValue(id, out var className))
                    {
                        return false;
                    }

                    executedCount++;
                    if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        failedCount++;
                    }
                    executedClasses.Add(className);
                    executedClasses.Add(className.Split('.').Last());
                }
            }

            return failedCount > 0 &&
                executedCount == candidateArm.ExecutedTestCount &&
                requiredTestClasses.All(required => executedClasses.Contains(required));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Xml.XmlException or ArgumentException or NotSupportedException or
                                   PathTooLongException or OverflowException)
        {
            return false;
        }
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

    private bool TryResolveFindingEvidenceCoverage(
        TaskSpec requestingTask,
        FindingEvidenceRequest normalizedRequest,
        string candidateSha,
        string executionBasisIdentity,
        out IReadOnlyList<FindingEvidenceReceipt> reusableReceipts,
        out IReadOnlyList<FindingEvidenceSelection> uncoveredSelections,
        out string coverageReason)
    {
        reusableReceipts = [];
        uncoveredSelections = normalizedRequest.Selections;
        coverageReason = string.Empty;
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
                outcome is { Honoured: true, ReceiptId.Length: > 0, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence })
            .GroupBy(outcome => outcome.ReceiptId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var latestReceiptBySelection = new Dictionary<string, FindingEvidenceReceipt>(StringComparer.Ordinal);
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
                if (!string.Equals(receipt.FindingRoundFingerprint, executedRoundFingerprint, StringComparison.Ordinal) ||
                    !string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(receipt.ExecutionBasisIdentity, executionBasisIdentity, StringComparison.Ordinal) ||
                    !TryGetFindingEvidenceCoverage(receipt.Request, out var receiptCoverage) ||
                    !receiptCoverage.Overlaps(requestedCoverage))
                {
                    continue;
                }

                // Preserve main's authority rule: the newest receipt which requested a selection
                // wins even when it is RED, incomplete, rejected, or otherwise non-reusable.
                foreach (var selection in receiptCoverage.Where(requestedCoverage.Contains))
                {
                    latestReceiptBySelection[selection] = receipt;
                }
            }
        }

        var reusableBySelection = new Dictionary<string, FindingEvidenceReceipt>(StringComparer.Ordinal);
        foreach (var selection in requestedCoverage)
        {
            if (latestReceiptBySelection.TryGetValue(selection, out var receipt) &&
                validOutcomesByReceiptId.ContainsKey(receipt.ReceiptId) &&
                IsReusableGreenFindingEvidenceReceipt(receipt, candidateSha, executionBasisIdentity))
            {
                reusableBySelection[selection] = receipt;
            }
        }

        uncoveredSelections = normalizedRequest.Selections
            .Where(selection => !reusableBySelection.ContainsKey(FormatFindingEvidenceSelection(selection)))
            .ToArray();
        reusableReceipts = normalizedRequest.Selections
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
            _ = TryGetFindingEvidenceCoverage(reusableReceipts[0].Request, out var sourceCoverage);
            coverageReason = requestedCoverage.SetEquals(sourceCoverage) ? "exact" : "subset";
        }

        return true;
    }

    private static bool IsReusableGreenFindingEvidenceReceipt(
        FindingEvidenceReceipt receipt,
        string candidateSha,
        string executionBasisIdentity)
    {
        if (!TryGetFindingEvidenceCoverage(receipt.Request, out var receiptCoverage))
        {
            return false;
        }

        var dispositions = receipt.RequestDispositions ?? [];
        var executedDispositions = dispositions
            .Where(disposition =>
                disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal) &&
                disposition.RequestIdentity
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.Ordinal)
                    .IsSupersetOf(receiptCoverage))
            .ToArray();
        if (receipt is not { Accepted: true, Passed: true } ||
            !string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.ExecutionBasisIdentity, executionBasisIdentity, StringComparison.Ordinal) ||
            executedDispositions.Length == 0 ||
            dispositions.Any(disposition =>
                executedDispositions.Any(executed =>
                    string.Equals(executed.RequestIdentity, disposition.RequestIdentity, StringComparison.Ordinal)) &&
                !disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal)))
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
                ResolveFindingEvidenceRequiredTestClasses(receipt.Request));
    }

    private static IReadOnlyList<string> ResolveFindingEvidenceRequiredTestClasses(FindingEvidenceRequest request)
    {
        var required = new List<string>();
        foreach (var selection in request.Selections)
        {
            if (!TryGetExpandableFindingEvidenceClass(selection.TestClass, out var testClass, out _))
            {
                return [];
            }

            required.Add(testClass);
        }

        return required.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static FindingEvidenceArmReceipt CreateFindingEvidenceArmReceipt(FocusedEvidenceArmRunResult arm)
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
            receiptArtifacts, arm.RestoredPaths, arm.DroppedPaths);
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

    private static string BuildFindingEvidenceExecutionBasisIdentity(AcceptanceGateEngineSettings settings)
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

    private static string BuildFindingRoundFingerprint(
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
        coverage = request.Selections
            .Where(selection =>
                !string.IsNullOrWhiteSpace(selection.TestProject) &&
                !string.IsNullOrWhiteSpace(selection.TestClass))
            .Select(FormatFindingEvidenceSelection)
            .ToHashSet(StringComparer.Ordinal);
        return coverage.Count > 0 && coverage.Count == request.Selections.Count;
    }

    private static bool HasEverCommittedOutput(TaskSpec task) =>
        task.VerificationHistory.Any(verification => verification.HasCommittedChanges) ||
        task.DispatchHistory.Any(dispatch =>
            !string.IsNullOrWhiteSpace(dispatch.ResultCommit) &&
            (string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
             !string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase)));

    // One refusal truth: routing and the worker-visible execution state read the same rule.
    private static bool IsPermanentFindingEvidenceRefusal(FindingEvidenceOutcome outcome) =>
        FindingEvidenceExecutionClassifier.IsPermanentRefusal(outcome);
}
