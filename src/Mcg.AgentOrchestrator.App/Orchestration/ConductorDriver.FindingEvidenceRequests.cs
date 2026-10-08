using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TryBuildFindingEvidenceRequest(
        Goal goal,
        TaskSpec requestingTask,
        ConductorAutonomyPolicy policy,
        out FailedGoalFindingObservation decision)
    {
        decision = FailedGoalFindingObservation.None;
        if (!WorkerResultBlockers.TryFindReviewFindingRound(requestingTask.LastVerification, out var round, out _))
        {
            return false;
        }

        var mergedFindings = requestingTask.LastVerification?.MergedReviewFindings ?? [];
        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        var candidateShaAvailable = ConductorGitRevisionReader.IsValid(candidateSha);
        var telemetryCandidateSha = candidateShaAvailable ? candidateSha! : "unavailable";
        var requestingFindings = round.Findings
            .Where(finding =>
                finding.State == ReviewFindingState.Open &&
                finding.EvidenceRequest is not null)
            .ToArray();
        if (requestingFindings.Length == 0)
        {
            return false;
        }

        var findingRoundFingerprint = BuildFindingRoundFingerprint(requestingTask, round);

        if (requestingTask.RequiredRole == AgentRole.Reviewer)
        {
            var openBlockingFindings = ReviewFindings.GetOpenBlockingFindings(
                mergedFindings.Count > 0 ? mergedFindings : round.Findings,
                goal.EffectiveAcceptanceCriteriaCorrections);
            var suppressionRoute = ReviewerFindingEvidenceSuppressionRouting.Resolve(
                requestingTask.LastVerification, goal.EffectiveAcceptanceCriteriaCorrections, openBlockingFindings);
            if (suppressionRoute.DeferToReviewRetryRoute)
            {
                return false;
            }
            var writableBlockerIds = suppressionRoute.WritableBlockerIds;
            var reviewedCandidateSha = requestingTask.LastVerification?.ReviewedCommit?.Trim();
            var requestTargetsCurrentCandidate =
                !candidateShaAvailable ||
                !ConductorGitRevisionReader.IsValid(reviewedCandidateSha) ||
                string.Equals(candidateSha, reviewedCandidateSha, StringComparison.OrdinalIgnoreCase);
            if (writableBlockerIds.Length > 0 && requestTargetsCurrentCandidate)
            {
                var reason = candidateShaAvailable ? "unresolved-writable-blockers-on-unchanged-candidate"
                    : "candidate-sha-unavailable-with-unresolved-writable-blockers";
                requestingFindings = ReviewerFindingEvidenceSuppressionRouting.SelectAndSuppress(
                    writableBlockerIds, requestingFindings, requestingTask, candidateSha,
                    BuildFindingEvidenceIdentity, requestIdentity => _recordFindingEvidenceSuppressed(
                        goal.Id, requestingTask.Id, telemetryCandidateSha, writableBlockerIds,
                        CreateFindingEvidenceRequestId(requestIdentity),
                        suppressionRoute.ChosenOwner ?? throw new InvalidOperationException("Writable finding suppression requires a feasible upstream owner."),
                        reason,
                        CreateFindingEvidenceSuppressionIdentity(telemetryCandidateSha,
                            requestIdentity, writableBlockerIds)));
                if (requestingFindings.Length == 0) return false;
            }
        }

        if (candidateShaAvailable && TryRouteCoveredPreTesterRequest(
                goal, requestingTask, candidateSha!, requestingFindings, out decision)) return true;

        var groups = new List<FindingEvidenceRequestGroup>();
        var normalizationRefused = false;
        var reusedGreenReceipt = false;
        var reattachedReceiptIds = new List<string>();
        var findingEvidenceEngineSettings = _getFindingEvidenceEngineSettings(goal);
        var executionBasisIdentity = BuildFindingEvidenceExecutionBasisIdentity(findingEvidenceEngineSettings);
        foreach (var finding in requestingFindings)
        {
            if (!TryNormalizeFindingEvidenceRequest(
                    finding.EvidenceRequest!, findingEvidenceEngineSettings,
                    (project, requestedClass) =>
                        _resolveFindingEvidenceSiblingClasses(goal, project, requestedClass),
                    out var typedRequest, out var request,
                    out var refusalReason, out var refusalDetail))
            {
                normalizationRefused = true;
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, refusalReason, refusalDetail, telemetryCandidateSha);
                continue;
            }

            var identity = BuildFindingEvidenceIdentity(typedRequest);
            IReadOnlyList<FindingEvidenceReceipt> reusedSourceReceipts = [];
            string? coverageDecisionReason = null;
            var mergedFinding = ReviewFindingConvergence.ResolveMergedFinding(
                mergedFindings, round, finding.StableId);
            var priorOutcome = mergedFinding?.EvidenceOutcome;
            if (priorOutcome is not null && IsPermanentFindingEvidenceRefusal(priorOutcome))
            {
                continue;
            }

            if (mergedFinding is not null &&
                HasCurrentFindingEvidenceReceipt(
                        requestingTask,
                        mergedFinding,
                        typedRequest,
                        telemetryCandidateSha,
                        findingRoundFingerprint,
                        executionBasisIdentity))
            {
                continue;
            }

            if (typedRequest.NegativeControl is null && TryResolveFindingEvidenceCoverage(
                    requestingTask,
                    typedRequest,
                    telemetryCandidateSha,
                    executionBasisIdentity,
                    out var coverageReceipts,
                    out var uncoveredSelections,
                    out var coverageReason))
            {
                reusedGreenReceipt = true;
                if (uncoveredSelections.Count == 0)
                {
                    ReattachCoverageReusableGreenFindingEvidence(
                        goal,
                        requestingTask,
                        mergedFinding ?? finding,
                        coverageReceipts,
                        identity,
                        coverageReason);
                    reattachedReceiptIds.AddRange(coverageReceipts.Select(receipt => receipt.ReceiptId));
                    continue;
                }

                typedRequest = typedRequest with { Selections = uncoveredSelections };
                request = string.Join("; ", uncoveredSelections.Select(FormatFindingEvidenceSelection));
                reusedSourceReceipts = coverageReceipts;
                coverageDecisionReason = coverageReason;
            }

            if (TryRouteReusableRedFindingEvidence(
                    goal, requestingTask, finding, typedRequest, request, identity,
                    telemetryCandidateSha, executionBasisIdentity, policy, findingRoundFingerprint, out decision))
            {
                return true;
            }

            var groupIndex = groups.FindIndex(group => string.Equals(group.Identity, identity, StringComparison.Ordinal));
            if (groupIndex < 0)
            {
                groups.Add(new FindingEvidenceRequestGroup(
                    identity,
                    request,
                    typedRequest,
                    [finding],
                    reusedSourceReceipts,
                    coverageDecisionReason));
            }
            else
            {
                groups[groupIndex].Findings.Add(finding);
            }
        }

        if (groups.Count == 0 && normalizationRefused)
        {
            decision = BuildCappedFindingEvidenceDeliveryRetry(
                goal, requestingTask, telemetryCandidateSha, requestingFindings, [],
                "Every current evidence request was refused during normalization; typed refusal details were attached.");
            return true;
        }

        if (groups.Count == 0 && reusedGreenReceipt)
        {
            decision = BuildReceiptClosureOrDeliveryRetry(
                goal, requestingTask, telemetryCandidateSha, requestingFindings,
                reattachedReceiptIds.Distinct(StringComparer.Ordinal).ToArray(),
                "Previously executed green evidence still matches the candidate and normalized request; its receipt was reattached without rerunning tests.");
            return true;
        }

        var batches = ExcludePassedRoundFindingEvidenceBatches(
            requestingTask, BuildFindingEvidenceBatches(groups), telemetryCandidateSha, findingRoundFingerprint);
        var runnable = batches.FirstOrDefault();
        if (runnable is null)
        {
            return false;
        }

        if (!candidateShaAvailable)
        {
            foreach (var group in batches)
            foreach (var finding in group.Findings)
            {
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, FindingEvidenceNotHonouredReason.CandidateShaMissing,
                    "No validated candidate SHA was available for the requested evidence run.",
                    telemetryCandidateSha);
            }
            decision = BuildCappedFindingEvidenceDeliveryRetry(
                goal, requestingTask, telemetryCandidateSha, requestingFindings, [],
                "Evidence requests could not run because the candidate SHA was unavailable.");
            return true;
        }

        if (!_focusedEvidenceRunnerConfigured)
        {
            foreach (var group in batches)
            foreach (var finding in group.Findings)
            {
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, FindingEvidenceNotHonouredReason.ExecutorUnavailable,
                    "No focused evidence executor was configured.", telemetryCandidateSha);
            }
            decision = BuildCappedFindingEvidenceDeliveryRetry(
                goal, requestingTask, telemetryCandidateSha, requestingFindings, [],
                "Evidence requests could not run because the executor was unavailable.");
            return true;
        }
        var initialRequestDispositions = BuildInitialRequestDispositions(batches, runnable);
        var executedRequestDispositions = initialRequestDispositions
            .Where(disposition => disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal))
            .ToArray();
        var requestContext = new ConductorFocusedEvidenceRequestContext(
            findingRoundFingerprint,
            CreateFindingEvidenceBatchId(candidateSha!, findingRoundFingerprint, policy.Name, runnable.Identity),
            initialRequestDispositions, NegativeControl: runnable.TypedRequest.NegativeControl, RevertPaths: runnable.TypedRequest.RevertPaths, Mutation: runnable.TypedRequest.Mutation);
        if (TryResumeUnconfirmedCandidateRed(
                goal, requestingTask, policy, runnable, candidateSha!, findingRoundFingerprint,
                requestContext, out decision))
        {
            return true;
        }
        if (!TryRestorePendingBaselineCandidate(goal, runnable.Request, candidateSha!, requestContext,
                out var evidence, out var evidenceAttempt) && !TryReconcileFocusedEvidenceAttempt(
                goal,
                policy,
                runnable.Request,
                candidateSha!,
                "finding-requested",
                requestContext,
                out evidence,
                out evidenceAttempt,
                out decision, out _))
        {
            if (decision.Kind == FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired)
            {
                foreach (var finding in runnable.Findings)
                {
                    RecordNotHonoured(
                        goal.Id, requestingTask, finding, FindingEvidenceNotHonouredReason.RunFailed,
                        decision.Evidence, telemetryCandidateSha);
                }
                decision = BuildCappedFindingEvidenceDeliveryRetry(
                    goal, requestingTask, telemetryCandidateSha, runnable.Findings, [],
                    "The focused evidence executor failed; a typed refusal was attached.");
            }
            return true;
        }

        if (!evidence.Accepted)
        {
            var reason = evidence.Rejection?.Code switch
            {
                FocusedEvidenceRejectionCode.UnsupportedProject =>
                    FindingEvidenceNotHonouredReason.UnsupportedProject,
                FocusedEvidenceRejectionCode.SourceDiscoveryFailure =>
                    FindingEvidenceNotHonouredReason.SelectionApparatusFailure,
                _ => FindingEvidenceNotHonouredReason.UnparseableSelection
            };
            var detail = evidence.Rejection is null
                ? evidence.Summary
                : $"{evidence.Rejection.Detail}; offending_filter='{evidence.Rejection.OffendingToken}'";
            foreach (var finding in runnable.Findings)
            {
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, reason,
                    detail, telemetryCandidateSha);
            }
            decision = BuildCappedFindingEvidenceDeliveryRetry(
                goal, requestingTask, telemetryCandidateSha, runnable.Findings, [],
                "The focused evidence executor did not accept the request.");
            return true;
        }

        var initialArmReceipts = (evidence.Arms ?? [])
            .Select(CreateFindingEvidenceArmReceipt)
            .ToArray();
        if (!TryResolveMissingBaseline(
                goal, requestingTask, policy, runnable, candidateSha!, findingRoundFingerprint, requestContext,
                evidence, initialArmReceipts,
                out evidence, out var armReceipts, out var receiptIdentity, out decision))
        {
            return true;
        }
        var receiptId = CreateFindingEvidenceReceiptId(
            candidateSha!, findingRoundFingerprint, receiptIdentity);
        var actionableCandidateRed = TryAttributeActionableCandidateRed(
            goal, candidateSha!, armReceipts, runnable);
        var requestDispositions = actionableCandidateRed is not null
            ? executedRequestDispositions
                .Concat(batches.Skip(1).SelectMany(batch => batch.Members).SelectMany(member =>
                    member.Findings.Select(finding => new FindingEvidenceRequestDisposition(
                        finding.StableId,
                        member.Identity,
                        "superseded",
                        "superseded-by-actionable-red"))))
                .ToArray()
            : initialRequestDispositions;
        var receipt = new FindingEvidenceReceipt(
            receiptId,
            candidateSha!,
            runnable.TypedRequest,
            evidence.Accepted,
            evidence.IsValidEvidence,
            evidence.Summary,
            armReceipts,
            requestDispositions,
            findingRoundFingerprint,
            executionBasisIdentity, evidence.NegativeControlOutcome, evidence.RevertPathsRejection);
        if (evidence.OutcomeReason == FindingEvidenceOutcomeReason.ApparatusFailure &&
            !armReceipts.Any(arm => arm is { Arm: FindingEvidenceArm.Candidate, Disposition: FindingEvidenceArmDisposition.Red }))
        {
            foreach (var finding in runnable.Findings)
            {
                _recordFindingEvidenceOutcome(
                    goal.Id,
                    requestingTask.Id,
                    finding.StableId,
                    new FindingEvidenceOutcome(
                        Honoured: false,
                        ReceiptId: receiptId,
                        Reason: FindingEvidenceNotHonouredReason.SelectionApparatusFailure,
                        Detail: evidence.Summary,
                        ResultReason: FindingEvidenceOutcomeReason.ApparatusFailure),
                    receipt);
                _recordFindingEvidenceRequest(
                    goal.Id,
                    requestingTask.Id,
                    $"finding-evidence disposition=not-honoured; role={requestingTask.RequiredRole}; " +
                    $"task_id={requestingTask.Id}; finding_id={finding.StableId}; candidate_sha={candidateSha}; " +
                    $"receipt_id={receiptId}; reason=selection-apparatus-failure; " +
                    $"detail={TrimForConductorMessage(evidence.Summary)}");
                _recordFindingEvidenceRun(
                    goal.Id,
                    requestingTask.Id,
                    $"finding-evidence apparatus-failure role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
                    $"finding_id={finding.StableId}; candidate_sha={candidateSha}; receipt_id={receiptId}; " +
                    FormatFocusedEvidenceResult(evidence));
            }
            if (evidenceAttempt is null ||
                !_focusedEvidenceAttemptCoordinator.RecordFocusedEvidenceRequestDispositions(
                    evidenceAttempt,
                    findingRoundFingerprint,
                    receiptId,
                    requestDispositions))
            {
                decision = FailedGoalFindingObservation.Observed(
                    FailedGoalFindingObservationKind.FindingEvidenceReceiptPersistenceFailed,
                    $"Focused evidence receipt {receiptId} was recorded, but its per-request apparatus dispositions could not be persisted; downstream routing stopped.");
                return true;
            }

            decision = BuildCappedFindingEvidenceDeliveryRetry(
                goal, requestingTask, candidateSha!, runnable.Findings, [receiptId],
                "Focused evidence selected zero tests; its apparatus receipt was attached for correction and reissue.");
            return true;
        }

        foreach (var finding in runnable.Findings)
        {
            var member = runnable.Members.Single(candidate => candidate.Findings.Contains(finding));
            var sourceReceiptIds = (member.ReusedSourceReceipts ?? [])
                .Select(sourceReceipt => sourceReceipt.ReceiptId)
                .Append(receiptId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var outcome = new FindingEvidenceOutcome(
                Honoured: true,
                ReceiptId: receiptId,
                ResultReason: evidence.OutcomeReason ?? FindingEvidenceOutcomeReason.Unknown,
                RequestedSelectionIdentity: member.Identity,
                DecisionReason: member.CoverageDecisionReason is null
                    ? "executed-focused-evidence"
                    : $"executed-uncovered-after-reuse:{member.CoverageDecisionReason}",
                SourceReceiptIds: sourceReceiptIds);
            foreach (var sourceReceipt in member.ReusedSourceReceipts ?? [])
            {
                _recordFindingEvidenceOutcome(
                    goal.Id, requestingTask.Id, finding.StableId, outcome, sourceReceipt);
            }
            _recordFindingEvidenceOutcome(
                goal.Id, requestingTask.Id, finding.StableId,
                outcome,
                receipt);
            var resultReason = FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(
                evidence.OutcomeReason ?? FindingEvidenceOutcomeReason.Unknown);
            _recordFindingEvidenceRequest(
                goal.Id, requestingTask.Id,
                $"finding-evidence disposition=honoured; role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
                $"finding_id={finding.StableId}; candidate_sha={candidateSha}; receipt_id={receiptId}; reason={resultReason}; " +
                $"decision={outcome.DecisionReason}; source_receipt_ids={string.Join(',', sourceReceiptIds)}");
            _recordFindingEvidenceRun(
                goal.Id, requestingTask.Id,
                $"finding-evidence role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; finding_id={finding.StableId}; " +
                $"candidate_sha={candidateSha}; receipt_id={receiptId}; reason={resultReason}; {FormatFocusedEvidenceResult(evidence)}");
        }

        if (actionableCandidateRed is not null)
        {
            foreach (var pending in batches.Skip(1))
            foreach (var member in pending.Members)
            foreach (var finding in member.Findings)
            {
                _recordFindingEvidenceOutcome(
                    goal.Id,
                    requestingTask.Id,
                    finding.StableId,
                    new FindingEvidenceOutcome(
                        Honoured: false,
                        ReceiptId: receiptId,
                        Reason: FindingEvidenceNotHonouredReason.SupersededByActionableRed,
                        Detail: $"Superseded by actionable candidate RED receipt {receiptId} at {candidateSha}.",
                        ResultReason: FindingEvidenceOutcomeReason.CandidateRed),
                    receipt);
                _recordFindingEvidenceRequest(
                    goal.Id,
                    requestingTask.Id,
                    $"finding-evidence disposition=superseded; role={requestingTask.RequiredRole}; " +
                    $"task_id={requestingTask.Id}; finding_id={finding.StableId}; candidate_sha={candidateSha}; " +
                    $"receipt_id={receiptId}; reason=superseded-by-actionable-red; request_identity={member.Identity}");
            }

        }

        if (evidenceAttempt is null ||
            !_focusedEvidenceAttemptCoordinator.RecordFocusedEvidenceRequestDispositions(
                evidenceAttempt,
                findingRoundFingerprint,
                receiptId,
                requestDispositions))
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidenceReceiptPersistenceFailed,
                $"Focused evidence receipt {receiptId} was recorded, but its per-request dispositions could not be persisted; downstream routing stopped.");
            return true;
        }

        if (actionableCandidateRed is not null)
        {
            decision = BuildActionableCandidateRedDecision(
                goal, requestingTask, candidateSha!, receiptId, actionableCandidateRed);
            return true;
        }
        if (TryBuildUnattributableRedEscalation(
                candidateSha!, armReceipts, runnable, receiptId, out decision))
        {
            return true;
        }

        decision = batches.Skip(1).Any()
            ? FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidencePending,
                "Focused evidence completed; another distinct request from the same finding round remains pending.")
            : BuildReceiptClosureOrDeliveryRetry(
                goal,
                requestingTask,
                candidateSha!,
                runnable.Findings,
                [receiptId],
                "Focused evidence completed and its receipt was attached to the requesting finding.");
        return true;
    }

    private void RecordNotHonoured(
        GoalId goalId,
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceNotHonouredReason reason,
        string detail,
        string candidateSha)
    {
        _recordFindingEvidenceOutcome(
            goalId, requestingTask.Id, finding.StableId,
            new FindingEvidenceOutcome(Honoured: false, Reason: reason, Detail: detail), null);
        _recordFindingEvidenceRequest(
            goalId, requestingTask.Id,
            $"finding-evidence disposition=not-honoured; role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
            $"finding_id={finding.StableId}; candidate_sha={candidateSha}; receipt_id=none; " +
            $"reason={FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(reason)}; detail={TrimForConductorMessage(detail)}");
    }

    private static bool TryNormalizeFindingEvidenceRequest(
        FindingEvidenceRequest request,
        AcceptanceGateEngineSettings engineSettings,
        Func<string, string, IReadOnlyList<string>> resolveSiblingClasses,
        out FindingEvidenceRequest normalized,
        out string executorRequest,
        out FindingEvidenceNotHonouredReason refusalReason,
        out string refusalDetail)
    {
        normalized = new FindingEvidenceRequest([]);
        executorRequest = string.Empty;
        refusalReason = FindingEvidenceNotHonouredReason.UnparseableSelection;
        refusalDetail = "Evidence request must contain at least one project/class selection.";
        if (request.Selections is not { Count: > 0 })
        {
            return false;
        }

        var selections = new List<FindingEvidenceSelection>();
        foreach (var selection in request.Selections)
        {
            if (selection is null)
            {
                refusalDetail = "Evidence selections cannot contain null entries.";
                return false;
            }
            var project = selection.TestProject?.Trim();
            var originalTestClass = FindingEvidenceNameSelector.ToFullyQualified(selection.TestClass ?? string.Empty);
            var testClass = originalTestClass.Trim();
            if (string.IsNullOrWhiteSpace(project) ||
                string.IsNullOrWhiteSpace(testClass) ||
                originalTestClass.Length > MaxFindingEvidenceFilterLength ||
                !IsSupportedFindingEvidenceFilter(testClass))
            {
                refusalDetail =
                    $"Every evidence selection requires a bounded valid test_project and test_class; " +
                    $"offending_filter='{originalTestClass}'.";
                return false;
            }

            if (!GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(
                    project,
                    engineSettings,
                    out var resolvedProject))
            {
                refusalReason = FindingEvidenceNotHonouredReason.UnsupportedProject;
                refusalDetail =
                    $"Focused evidence does not support test project '{project}'. Accepted forms: " +
                    $"{GoalAcceptanceVerifier.FocusedEvidenceSupportedProjectForms(engineSettings)}.";
                return false;
            }

            var canonicalProject = GoalAcceptanceVerifier.ProjectLabel(resolvedProject);
            selections.Add(new FindingEvidenceSelection(canonicalProject, originalTestClass));
            if (TryGetExpandableFindingEvidenceClass(testClass, out var requestedClass, out var filterPrefix))
            {
                foreach (var siblingClass in resolveSiblingClasses(resolvedProject, requestedClass))
                {
                    if (string.IsNullOrWhiteSpace(siblingClass) ||
                        !EvidenceBareClassNamePattern.IsMatch(siblingClass) ||
                        siblingClass.Contains('+') ||
                        siblingClass.Contains('`'))
                    {
                        continue;
                    }

                    selections.Add(new FindingEvidenceSelection(
                        canonicalProject,
                        filterPrefix + siblingClass));
                }
            }
        }

        var distinct = selections
            .Distinct()
            .OrderBy(selection => selection.TestProject, StringComparer.Ordinal)
            .ThenBy(selection => selection.TestClass, StringComparer.Ordinal)
            .ToArray();
        normalized = request with { Selections = distinct };
        executorRequest = string.Join(
            "; ",
            distinct.Select(FormatFindingEvidenceSelection));
        return true;
    }

    private static bool TryGetExpandableFindingEvidenceClass(
        string filter,
        out string requestedClass,
        out string filterPrefix)
    {
        const string fullyQualifiedNamePrefix = "FullyQualifiedName~";
        filterPrefix = string.Empty;
        requestedClass = filter;
        if (filter.StartsWith(fullyQualifiedNamePrefix, StringComparison.Ordinal))
        {
            filterPrefix = fullyQualifiedNamePrefix;
            requestedClass = filter[fullyQualifiedNamePrefix.Length..];
        }

        return EvidenceBareClassNamePattern.IsMatch(requestedClass) &&
            !requestedClass.Contains('+') &&
            !requestedClass.Contains('`');
    }

    private static bool IsSupportedFindingEvidenceFilter(string filter)
    {
        if (EvidenceBareClassNamePattern.IsMatch(filter))
        {
            return true;
        }

        var parenthesisDepth = 0;
        foreach (var character in filter)
        {
            if (character == '(')
            {
                parenthesisDepth++;
            }
            else if (character == ')' && --parenthesisDepth < 0)
            {
                return false;
            }
        }
        if (parenthesisDepth != 0)
        {
            return false;
        }

        var tokens = Regex.Split(filter, @"[&|]");
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        var hasPositiveSelection = false;
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim().Trim('(', ')').Trim();
            if (token.Length == 0 ||
                token.Contains('(') ||
                token.Contains(')') ||
                !EvidenceFilterTokenPattern.IsMatch(token))
            {
                return false;
            }

            hasPositiveSelection |= token.Contains("FullyQualifiedName~", StringComparison.OrdinalIgnoreCase);
        }

        return hasPositiveSelection;
    }

    private static string FormatFindingEvidenceSelection(FindingEvidenceSelection selection) =>
        selection.TestProject + ":" + selection.TestClass;

    private static string BuildFindingEvidenceIdentity(FindingEvidenceRequest request) =>
        FindingEvidenceExecutionClassifier.BuildRequestIdentity(request);

    private static IReadOnlyList<FindingEvidenceRequestDisposition> BuildInitialRequestDispositions(
        IReadOnlyList<FindingEvidenceBatch> batches,
        FindingEvidenceBatch executingBatch)
    {
        var dispositions = new List<FindingEvidenceRequestDisposition>();
        foreach (var batch in batches)
        {
            var isExecuting = ReferenceEquals(batch, executingBatch);
            var disposition = isExecuting
                ? batch.Members.Count > 1 ? "executed-batched" : "executed-standalone"
                : batch.Members.Count > 1 ? "pending-batched" : "pending-standalone";
            var reason = batch.Members.Count > 1
                ? "compatible-same-project"
                : ResolveStandaloneEvidenceReason(batches, batch);
            dispositions.AddRange(batch.Members.SelectMany(member =>
                member.Findings.Select(finding => new FindingEvidenceRequestDisposition(
                    finding.StableId,
                    member.Identity,
                    disposition,
                    reason))));
        }

        return dispositions;
    }

    private static string ResolveStandaloneEvidenceReason(
        IReadOnlyList<FindingEvidenceBatch> batches,
        FindingEvidenceBatch standaloneBatch)
    {
        foreach (var other in batches.Where(batch => !ReferenceEquals(batch, standaloneBatch)))
        {
            var reason = GetFindingEvidenceUnbatchedReason(standaloneBatch.TypedRequest, other.TypedRequest);
            if (reason is not null)
            {
                return reason;
            }
        }

        return "single-request";
    }

    private static IReadOnlyList<FindingEvidenceBatch> BuildFindingEvidenceBatches(
        IReadOnlyList<FindingEvidenceRequestGroup> groups)
    {
        var batches = new List<FindingEvidenceBatch>();
        foreach (var group in groups)
        {
            var batchIndex = batches.FindIndex(batch =>
                GetFindingEvidenceUnbatchedReason(batch.TypedRequest, group.TypedRequest) is null);
            if (batchIndex < 0)
            {
                batches.Add(new FindingEvidenceBatch(
                    group.Identity,
                    group.Request,
                    group.TypedRequest,
                    [.. group.Findings],
                    [group]));
                continue;
            }

            var batch = batches[batchIndex];
            var selections = batch.TypedRequest.Selections
                .Concat(group.TypedRequest.Selections)
                .Distinct()
                .OrderBy(selection => selection.TestProject, StringComparer.Ordinal)
                .ThenBy(selection => selection.TestClass, StringComparer.Ordinal)
                .ToArray();
            var request = batch.TypedRequest with { Selections = selections };
            batches[batchIndex] = new FindingEvidenceBatch(
                BuildFindingEvidenceIdentity(request),
                string.Join("; ", selections.Select(FormatFindingEvidenceSelection)),
                request,
                [.. batch.Findings, .. group.Findings],
                [.. batch.Members, group]);
        }

        return batches;
    }

    private static string? GetFindingEvidenceUnbatchedReason(
        FindingEvidenceRequest left,
        FindingEvidenceRequest right)
    {
        if (left.NegativeControl != right.NegativeControl) return "different-negative-control";
        if (!FindingEvidenceRevertPaths.SamePaths(left.RevertPaths, right.RevertPaths)) return "different-revert-paths";
        if (!FindingEvidenceMutation.Same(left.Mutation, right.Mutation)) return "different-mutation";
        var leftProjects = left.Selections
            .Select(selection => selection.TestProject)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rightProjects = right.Selections
            .Select(selection => selection.TestProject)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (leftProjects.Length != 1 || rightProjects.Length != 1)
        {
            return "mixed-test-projects";
        }

        if (!string.Equals(leftProjects[0], rightProjects[0], StringComparison.OrdinalIgnoreCase))
        {
            return "different-test-project";
        }

        return left.Selections.Concat(right.Selections).All(selection =>
            EvidenceBareClassNamePattern.IsMatch(selection.TestClass.Trim()))
                ? null
                : "incompatible-filter-semantics";
    }

    private ActionableCandidateRedAttribution? TryAttributeActionableCandidateRed(
        Goal goal,
        string candidateSha,
        IReadOnlyList<FindingEvidenceArmReceipt> arms,
        FindingEvidenceBatch batch)
    {
        var candidate = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var baseline = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        if (candidate is not
                {
                    Accepted: true,
                    Disposition: FindingEvidenceArmDisposition.Red,
                    FailingTestIdentities.Count: > 0
                } ||
            !string.Equals(candidate.Sha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
            (baseline is null
                ? !EveryFailingTestIsInsideCandidateChanges(goal, batch, candidate.FailingTestIdentities)
                : baseline.Disposition != FindingEvidenceArmDisposition.Green))
        {
            return null;
        }

        var attributableFindings = new List<ReviewFinding>();
        var attributableFailingTests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in batch.Members)
        {
            var matchingFailures = batch.Members.Count == 1
                ? candidate.FailingTestIdentities
                : candidate.FailingTestIdentities!
                    .Where(failingIdentity => member.TypedRequest.Selections.Any(selection =>
                        IsTestIdentitySelectionMatch(failingIdentity, selection.TestClass.Trim())))
                    .ToArray();
            if (matchingFailures is not { Count: > 0 })
            {
                continue;
            }

            var developerFindings = member.Findings.Where(finding =>
                finding.Category is not (
                    FindingCategory.OperatorOwned or
                    FindingCategory.SpecDefect or
                    FindingCategory.AcceptanceOwned) &&
                IsDeveloperOwnedFindingAnchor(finding.Location.File));
            foreach (var finding in developerFindings)
            {
                attributableFindings.Add(finding);
                attributableFailingTests.UnionWith(matchingFailures);
            }
        }

        return attributableFindings.Count == 0
            ? null
            : new ActionableCandidateRedAttribution(
                attributableFindings.DistinctBy(finding => finding.StableId).ToArray(),
                attributableFailingTests.ToArray(),
                candidate.TestResultPaths ?? []);
    }

    private static bool IsDeveloperOwnedFindingAnchor(string path) =>
        RepositoryOwnershipMap.Classify(path).Area is
            RepositoryOwnershipArea.Source or
            RepositoryOwnershipArea.Test or
            RepositoryOwnershipArea.SharedInfrastructure;

    private static bool IsTestIdentitySelectionMatch(string identity, string selection)
    {
        var start = 0;
        while ((start = identity.IndexOf(selection, start, StringComparison.Ordinal)) >= 0)
        {
            var end = start + selection.Length;
            var leftBoundary = start == 0 || identity[start - 1] is '.' or '+';
            var rightBoundary = end == identity.Length || identity[end] is '.' or '+' or '(' or '[';
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            start++;
        }

        return false;
    }

    private static string CreateFindingEvidenceReceiptId(
        string candidateSha,
        string findingRoundFingerprint,
        string identity) =>
        "finding-evidence-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{candidateSha}:{findingRoundFingerprint}:{identity}")))
            .ToLowerInvariant()[..24];

    private static string CreateFindingEvidenceRequestId(string identity) =>
        "evidence-request-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..20];

    private static string CreateFindingEvidenceSuppressionIdentity(
        string candidateSha,
        string requestIdentity,
        IReadOnlyList<string> writableBlockerIds)
    {
        var blockerIdentity = string.Join(
            "\u001f",
            writableBlockerIds
                .OrderBy(id => id, StringComparer.Ordinal));
        return "evidence-suppression-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{candidateSha.Trim().ToLowerInvariant()}\u001e{requestIdentity}\u001e{blockerIdentity}")))
            .ToLowerInvariant()[..24];
    }

    private static string CreateFindingEvidenceBatchId(
        string candidateSha,
        string findingRoundFingerprint,
        string policyName,
        string identity) =>
        "evidence-batch-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{candidateSha}:{findingRoundFingerprint}:{policyName}:{identity}")))
            .ToLowerInvariant()[..16];

    internal static ConductorAdvanceOutcome.Held FocusedEvidencePendingHeld(
        GoalLifecycleState state,
        FailedGoalFindingObservation decision,
        ConductorParallelAcceptanceAttemptDecisionKind? kind) =>
        new(state, decision.Evidence)
        {
            Owner = kind == ConductorParallelAcceptanceAttemptDecisionKind.Running
                ? ConductorHoldOwner.BackgroundAttempt
                : ConductorHoldOwner.None
        };

    private sealed record FindingEvidenceRequestGroup(
        string Identity,
        string Request,
        FindingEvidenceRequest TypedRequest,
        List<ReviewFinding> Findings,
        IReadOnlyList<FindingEvidenceReceipt>? ReusedSourceReceipts = null,
        string? CoverageDecisionReason = null);

    private sealed record FindingEvidenceBatch(
        string Identity,
        string Request,
        FindingEvidenceRequest TypedRequest,
        List<ReviewFinding> Findings,
        IReadOnlyList<FindingEvidenceRequestGroup> Members);

    private sealed record ActionableCandidateRedAttribution(
        IReadOnlyList<ReviewFinding> Findings,
        IReadOnlyList<string> FailingTestIdentities,
        IReadOnlyList<string> CandidateTestResultPaths);
}
