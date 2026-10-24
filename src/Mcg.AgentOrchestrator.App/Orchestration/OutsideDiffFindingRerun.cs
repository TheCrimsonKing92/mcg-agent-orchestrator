using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the allowance and evidence decision for one finding, never a worker task or a test verdict.
internal static class OutsideDiffFindingRerun
{
    internal const string DecisionReason = "outside-diff-red-rerun-green";
    internal enum DiffRelationship { Indeterminate, Inside, Outside }
    private sealed record RedIdentity(string Identity, string Project, string ReceiptId);
    private sealed record Rerun(ReviewFinding Finding, string Key, RedIdentity[] Identities,
        FindingEvidenceRequest Request, string ExecutorRequest);

    internal static bool TryRoute(ConductorDriver driver, Goal goal, ConductorAutonomyPolicy policy,
        TaskSpec tester, TaskSpec? developer, IReadOnlyList<ReviewFinding>? findings,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        if (!driver._focusedEvidenceRunnerConfigured || developer is null || HasUnconsumedRetry(developer) ||
            tester is not { RequiredRole: AgentRole.Tester, Status: WorkTaskStatus.Failed } ||
            findings is not { Count: > 0 }) return false;
        var sha = driver._getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!ConductorGitRevisionReader.IsValid(sha)) return false;
        var changed = driver._getLandingFileScopes(goal).Select(NormalizeFindingEvidencePath)
            .Where(path => path.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceRoot = driver._executionDirectory ?? Directory.GetCurrentDirectory();
        var rootSources = new AcceptanceTestSourceResolver.Request(sourceRoot);
        var worktree = GoalWorktrees.TryResolve(sourceRoot, goal.Id);
        var goalSources = worktree is null ? null : new AcceptanceTestSourceResolver.Request(worktree);
        IReadOnlyList<string> Resolve(string project, string identity) =>
            (goalSources?.ResolveSourcePaths(project, identity) ?? [])
                .Concat(rootSources.ResolveSourcePaths(project, identity)).ToArray();
        var reds = CollectRedIdentities(driver, goal, tester, sha!);
        var reruns = new List<Rerun>();
        foreach (var finding in findings.OrderBy(item => item.StableId, StringComparer.Ordinal))
        {
            if (finding.EvidenceRequest is not null || finding.State != ReviewFindingState.Open ||
                ReviewFindingRouting.Project([finding]).Single().TargetRole != AgentRole.Developer ||
                HasAutoResolution(goal, finding.StableId)) continue;
            var names = NamedIdentities(finding);
            if (names.Length == 0) continue;
            var identities = names.Select(name => reds.FirstOrDefault(red => red.Identity == name)).ToArray();
            if (identities.Any(item => item is null) || identities.Any(item =>
                ClassifyIdentity(changed, Resolve, item!.Project, item.Identity) != DiffRelationship.Outside)) continue;
            var selected = identities.Select(item => item!).ToArray();
            var selections = selected.Select(item => new FindingEvidenceSelection(item.Project, ClassOf(item.Identity)))
                .Distinct().ToArray();
            if (!TryNormalizeFindingEvidenceRequest(new FindingEvidenceRequest(selections),
                driver._getFindingEvidenceEngineSettings(goal), (_, _) => [], out var request,
                out var executor, out _, out _)) continue;
            reruns.Add(new(finding, BuildKey(sha!, finding.StableId), selected, request, executor));
        }
        if (reruns.Count == 0) return false;

        var completed = new List<(Rerun Rerun, FindingEvidenceReceipt Receipt)>();
        foreach (var rerun in reruns)
        {
            if (!Execute(driver, goal, policy, tester, sha!, rerun, out var receipt, out observation)) return true;
            completed.Add((rerun, receipt!));
        }
        // No source retry gets ahead of another eligible finding's pending reproduction.
        var unclosable = completed.Where(item => !CanResolve(sha!, item.Receipt.Arms ?? [],
            item.Rerun.Identities.Select(red => red.Identity).ToArray())).ToArray();
        if (unclosable.Length > 0)
        {
            var decisions = new List<FailedGoalFindingObservation>();
            foreach (var item in unclosable)
            {
                var batch = Batch(item.Rerun);
                RouteReceipt(driver, goal, tester, developer, sha!, item.Rerun, batch, item.Receipt, out var decision);
                decisions.Add(decision);
            }
            observation = decisions.FirstOrDefault(item => item.Kind == FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired)
                ?? decisions.First();
            return true;
        }
        observation = driver.BuildReceiptClosureOrDeliveryRetry(goal, tester, sha!,
            completed.Select(item => item.Rerun.Finding).ToArray(), completed.Select(item => item.Receipt.ReceiptId).ToArray(),
            "Outside-diff identities passed conductor reproduction with usable non-red baselines.");
        if (observation.Evidence.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal))
            foreach (var item in completed)
            {
                RaiseNotice(driver, goal, sha!, item.Rerun, item.Receipt);
                driver._recordFindingEvidenceRun(goal.Id, tester.Id,
                    $"{Marker("resolved", item.Rerun.Key)} candidate={sha}; stable_id={Uri.EscapeDataString(item.Rerun.Finding.StableId)}; receipt_id={item.Receipt.ReceiptId}");
            }
        return true;
    }

    private static bool Execute(ConductorDriver driver, Goal goal, ConductorAutonomyPolicy policy,
        TaskSpec tester, string sha, Rerun rerun, out FindingEvidenceReceipt? completedReceipt,
        out FailedGoalFindingObservation observation)
    {
        completedReceipt = null;
        observation = FailedGoalFindingObservation.None;
        var identity = BuildFindingEvidenceIdentity(rerun.Request);
        var dispositions = new[] { new FindingEvidenceRequestDisposition(rerun.Finding.StableId, identity,
            "executed", DecisionReason, FindingEvidenceRequestDecision.Executed, sha) };
        var context = new ConductorFocusedEvidenceRequestContext(DecisionReason,
            CreateFindingEvidenceBatchId(sha, DecisionReason, DecisionReason, rerun.Key), dispositions, RunBaselineArm: true);
        var batch = Batch(rerun);
        if (driver.TryResumeUnconfirmedCandidateRed(goal, tester, policy, batch, sha, rerun.Key, context, out observation))
            return false;
        var receiptId = CreateFindingEvidenceReceiptId(sha, DecisionReason, rerun.Key);
        var marker = Marker("request", rerun.Key);
        var requested = goal.Timeline.Any(evt => evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.StartsWith(marker, StringComparison.Ordinal));
        var recorded = tester.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
            .LastOrDefault(receipt => receipt.ReceiptId == receiptId);
        if (recorded is not null && goal.Timeline.Any(evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith(Marker("receipt", rerun.Key), StringComparison.Ordinal)))
        {
            completedReceipt = recorded;
            return true;
        }

        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], sha, null);
        var priorAttempt = driver._focusedEvidenceAttemptCoordinator.ReadLatestFocusedEvidenceAttempt(candidate, context.BatchId);
        if (requested && priorAttempt is null)
        {
            observation = Unusable(receiptId, "recorded request has no recoverable attempt");
            return false;
        }
        var complete = driver.TryReconcileFocusedEvidenceAttempt(goal, policy, rerun.ExecutorRequest, sha,
            DecisionReason, context, out var evidence, out var attempt, out observation, out var kind);
        if (attempt?.FocusedEvidenceBatchId == context.BatchId && (complete || kind is
            ConductorParallelAcceptanceAttemptDecisionKind.Started or ConductorParallelAcceptanceAttemptDecisionKind.Running))
        {
            if (!requested)
                driver._recordFindingEvidenceRequest(goal.Id, tester.Id,
                    $"{marker} candidate={sha}; stable_id={Uri.EscapeDataString(rerun.Finding.StableId)}; " +
                    $"identities={Encode(rerun.Identities.Select(item => item.Identity))}; " +
                    $"original_receipts={Encode(rerun.Identities.Select(item => item.ReceiptId))}; selections={rerun.ExecutorRequest}");
        }
        if (complete && !requested)
        {
            observation = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                $"Outside-diff request {rerun.Key} committed; consume its receipt on the next advance.");
            return false;
        }
        if (!complete) return false;
        // Recheck currency after execution; an old candidate must produce no routing or closure.
        if (!string.Equals(driver._getPreReviewEvidenceContext(goal).CandidateSha?.Trim(), sha, StringComparison.OrdinalIgnoreCase))
        {
            if (attempt is not null) driver._focusedEvidenceAttemptCoordinator.MarkReconciled(attempt);
            observation = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                $"Outside-diff evidence for superseded candidate {sha} discarded.");
            return false;
        }
        var initialArms = (evidence.Arms ?? []).Select(CreateFindingEvidenceArmReceipt).ToArray();
        if (!driver.TryResolveMissingBaseline(goal, tester, policy, batch, sha, rerun.Key, context,
            evidence, initialArms, out evidence, out var arms, out _, out observation, receiptId)) return false;
        if (!string.Equals(driver._getPreReviewEvidenceContext(goal).CandidateSha?.Trim(), sha, StringComparison.OrdinalIgnoreCase))
        {
            if (attempt is not null) driver._focusedEvidenceAttemptCoordinator.MarkReconciled(attempt);
            observation = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                $"Outside-diff evidence for superseded candidate {sha} discarded.");
            return false;
        }
        var green = CanResolve(sha, arms, rerun.Identities.Select(item => item.Identity).ToArray());
        var candidateArm = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var usableCandidate = candidateArm is { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red } ||
            candidateArm is { Accepted: true, Passed: true, Disposition: FindingEvidenceArmDisposition.Green } &&
            FindingEvidenceTrxCoverage.HasPassedIdentities(candidateArm, rerun.Identities.Select(item => item.Identity).ToArray());
        var receipt = new FindingEvidenceReceipt(receiptId, sha, rerun.Request, evidence.Accepted,
            evidence.IsValidEvidence, evidence.Summary, arms, dispositions, DecisionReason,
            BuildFindingEvidenceExecutionBasisIdentity(driver._getFindingEvidenceEngineSettings(goal)));
        var outcome = new FindingEvidenceOutcome(evidence.Accepted && usableCandidate, receiptId,
            Reason: usableCandidate ? null : FindingEvidenceNotHonouredReason.SelectionApparatusFailure, Detail: evidence.Summary,
            ResultReason: green ? FindingEvidenceOutcomeReason.ValidEvidence : usableCandidate ? evidence.OutcomeReason : FindingEvidenceOutcomeReason.ApparatusFailure,
            RequestedSelectionIdentity: identity, DecisionReason: green ? DecisionReason : null,
            SourceReceiptIds: rerun.Identities.Select(item => item.ReceiptId).Append(receiptId).Distinct().ToArray());
        driver._recordFindingEvidenceOutcome(goal.Id, tester.Id, rerun.Finding.StableId, outcome, receipt);
        if (attempt is null || !driver._focusedEvidenceAttemptCoordinator.RecordFocusedEvidenceRequestDispositions(
            attempt, DecisionReason, receiptId, dispositions))
            throw new InvalidOperationException($"Could not persist outside-diff rerun receipt {receiptId}.");
        driver._recordFindingEvidenceRun(goal.Id, tester.Id,
            $"{Marker("receipt", rerun.Key)} candidate={sha}; stable_id={Uri.EscapeDataString(rerun.Finding.StableId)}; " +
            $"receipt_id={receiptId}; outcome={(green ? "green" : "not-closable")}");
        driver._focusedEvidenceAttemptCoordinator.MarkReconciled(attempt);
        completedReceipt = receipt;
        return true;
    }

    private static FindingEvidenceBatch Batch(Rerun rerun)
    {
        var identity = BuildFindingEvidenceIdentity(rerun.Request);
        var group = new FindingEvidenceRequestGroup(identity, rerun.ExecutorRequest, rerun.Request, [rerun.Finding]);
        return new(identity, rerun.ExecutorRequest, rerun.Request, [rerun.Finding], [group]);
    }

    private static bool RouteReceipt(ConductorDriver driver, Goal goal, TaskSpec tester, TaskSpec developer,
        string sha, Rerun rerun, FindingEvidenceBatch batch, FindingEvidenceReceipt receipt,
        out FailedGoalFindingObservation observation)
    {
        var arms = receipt.Arms ?? [];
        var attribution = driver.TryAttributeActionableCandidateRed(goal, sha, arms, batch);
        if (attribution is not null)
        {
            observation = BuildActionableCandidateRedDecision(goal, tester, sha, receipt.ReceiptId, attribution,
                receipt.Summary);
            return true;
        }
        if (TryBuildUnattributableRedEscalation(sha, arms, batch, receipt.ReceiptId, out observation)) return true;
        var candidate = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var baseline = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        // This combination has no closure authority; deliver both arms to the existing Developer owner.
        if (candidate is { Accepted: true, Passed: true, Disposition: FindingEvidenceArmDisposition.Green } &&
            baseline is { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red } &&
            FindingEvidenceTrxCoverage.HasPassedIdentities(candidate, rerun.Identities.Select(item => item.Identity).ToArray()))
        {
            observation = BuildCappedFindingEvidenceDeliveryRetry(goal, developer, sha, [rerun.Finding],
                [receipt.ReceiptId], $"Candidate green; Baseline red; {receipt.Summary}");
            return true;
        }
        observation = BuildCappedFindingEvidenceDeliveryRetry(goal, tester, sha, [rerun.Finding], [receipt.ReceiptId],
            "Outside-diff reproduction is unusable: exact identity Passed or usable baseline evidence unavailable.");
        return true;
    }

    internal static bool CanResolve(string sha, IReadOnlyList<FindingEvidenceArmReceipt> arms, IReadOnlyList<string> identities)
    {
        var candidate = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var baseline = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        return candidate is { Accepted: true, Passed: true, Disposition: FindingEvidenceArmDisposition.Green } &&
            string.Equals(candidate.Sha, sha, StringComparison.OrdinalIgnoreCase) &&
            baseline is { Accepted: true, Passed: true, Disposition: FindingEvidenceArmDisposition.Green } &&
            baseline.ExecutedTestCount is > 0 && baseline.NotRunSelections.Count == 0 &&
            AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(baseline.TestResultPaths,
                baseline.ReceiptArtifacts, baseline.ExecutedTestCount.Value, identities.Select(ClassOf).Distinct().ToArray()) &&
            candidate.NotRunSelections.Count == 0 && FindingEvidenceTrxCoverage.HasPassedIdentities(candidate, identities);
    }

    internal static DiffRelationship ClassifyIdentity(IReadOnlySet<string> changed,
        Func<string, string, IReadOnlyList<string>> resolve, string project, string identity)
    {
        if (changed.Count == 0) return DiffRelationship.Indeterminate;
        var paths = resolve(project, identity).Select(NormalizeFindingEvidencePath).Where(path => path.Length > 0).ToArray();
        return paths.Length == 0 ? DiffRelationship.Indeterminate :
            paths.Any(changed.Contains) ? DiffRelationship.Inside : DiffRelationship.Outside;
    }

    private static RedIdentity[] CollectRedIdentities(ConductorDriver driver, Goal goal, TaskSpec tester, string sha)
    {
        var receipts = goal.Tasks.SelectMany(task => task.VerificationHistory)
            .SelectMany(record => record.FindingEvidenceReceipts ?? []).ToList();
        if (PreTesterEvidenceIndexLines.Latest(goal, tester.Id, sha) is { Outcome: "red" or "actionable-red" } entry &&
            driver._focusedEvidenceAttemptCoordinator.ReadFocusedReceiptEvidence(goal.Id, entry.ReceiptId, sha) is { } evidence)
        {
            var selections = entry.Selections.Select(value => value.Split(':', 2))
                .Where(parts => parts.Length == 2).Select(parts => new FindingEvidenceSelection(parts[0], parts[1])).ToArray();
            receipts.Add(new(entry.ReceiptId, sha, new(selections), evidence.Accepted, evidence.Passed, evidence.Summary,
                (evidence.Arms ?? []).Select(CreateFindingEvidenceArmReceipt).ToArray()));
        }
        var settings = driver._getFindingEvidenceEngineSettings(goal);
        return receipts.Where(receipt => receipt.Accepted && string.Equals(receipt.CandidateSha, sha, StringComparison.OrdinalIgnoreCase))
            .SelectMany(receipt => (receipt.Arms ?? []).Where(arm => arm is
                { Arm: FindingEvidenceArm.Candidate, Accepted: true, Disposition: FindingEvidenceArmDisposition.Red } &&
                string.Equals(arm.Sha, sha, StringComparison.OrdinalIgnoreCase) &&
                FindingEvidenceTrxCoverage.HasContentBoundRedTrxCoverage(arm,
                    (arm.FailingTestIdentities ?? []).Select(ClassOf).Distinct().ToArray()))
                .SelectMany(arm => (arm.FailingTestIdentities ?? []).SelectMany(identity => receipt.Request.Selections
                    .Where(selection => SelectionContains(selection, identity))
                    .Where(selection => GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(selection.TestProject, settings, out _))
                    .Select(selection =>
                    {
                        GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(selection.TestProject, settings, out var project);
                        return new RedIdentity(identity, project, receipt.ReceiptId);
                    })) ))
            .DistinctBy(item => item.Identity).ToArray();
    }

    private static bool SelectionContains(FindingEvidenceSelection selection, string identity)
    {
        var selected = selection.TestClass.Replace("FullyQualifiedName~", "", StringComparison.Ordinal).Trim();
        var testClass = ClassOf(identity);
        return selected == testClass || testClass.EndsWith("." + selected, StringComparison.Ordinal);
    }

    private static string ClassOf(string identity)
    {
        var method = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
        var dot = method.LastIndexOf('.');
        return dot < 0 ? "" : method[..dot];
    }

    // Extract full identities, including data-case suffixes. Unknown named tests invalidate the finding.
    private static string[] NamedIdentities(ReviewFinding finding) => Regex.Matches(
        finding.Description + "\n" + finding.Location.Region,
        @"\b[A-Za-z_]\w*(?:[.+][A-Za-z_]\w*)+(?:\([^\r\n)]*\)|\[[^\r\n\]]*\])?")
        .Select(match => match.Value).Where(value => !new[] { ".cs", ".csproj", ".trx", ".dll" }
            .Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        .Distinct(StringComparer.Ordinal).ToArray();

    internal static string BuildKey(string sha, string stableId) =>
        FailedGoalTimedOutSelectionRerunRule.BuildKey(sha.ToLowerInvariant(), [stableId]);
    private static string Marker(string phase, string key) => $"{DecisionReason}; phase={phase}; key={key};";
    private static string Encode(IEnumerable<string> values) => string.Join(',', values.Distinct().Select(Uri.EscapeDataString));

    private static bool HasAutoResolution(Goal goal, string stableId) => goal.Tasks.SelectMany(task => task.VerificationHistory)
        .SelectMany(record => record.MergedReviewFindings ?? []).Any(finding => finding.StableId == stableId &&
            finding.State == ReviewFindingState.Resolved && finding.EvidenceOutcome?.DecisionReason == DecisionReason) ||
        goal.Timeline.Any(evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith($"{DecisionReason}; phase=resolved;", StringComparison.Ordinal) &&
            evt.Message.Contains($"; stable_id={Uri.EscapeDataString(stableId)};", StringComparison.Ordinal));

    internal static bool AuthorizesClosure(Goal goal, TaskSpec task, ReviewFinding finding, string sha)
    {
        if (finding.EvidenceRequest is not null || HasAutoResolution(goal, finding.StableId) ||
            finding.EvidenceOutcome is not { Honoured: true, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
            DecisionReason: DecisionReason, ReceiptId: { } id }) return false;
        var key = BuildKey(sha, finding.StableId);
        var receipt = task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
            .LastOrDefault(item => item.ReceiptId == id && string.Equals(item.CandidateSha, sha, StringComparison.OrdinalIgnoreCase));
        var request = goal.Timeline.LastOrDefault(evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded && evt.Message.StartsWith(Marker("request", key), StringComparison.Ordinal));
        if (receipt is null || request is null || !goal.Timeline.Any(evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded && evt.Message.StartsWith(Marker("receipt", key), StringComparison.Ordinal) &&
            evt.Message.Contains($"; receipt_id={id}; outcome=green", StringComparison.Ordinal))) return false;
        var field = request.Message.Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(value => value.StartsWith("identities=", StringComparison.Ordinal));
        var identities = field?["identities=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray() ?? [];
        return CanResolve(sha, receipt.Arms ?? [], identities);
    }

    internal static string FormatClosureDecision(IReadOnlyList<(ReviewFinding Finding, FindingEvidenceReceipt Receipt)> closable)
    {
        var ids = closable.Where(pair => pair.Finding.EvidenceOutcome?.DecisionReason == DecisionReason)
            .Select(pair => pair.Finding.StableId).ToArray();
        return ids.Length == 0 ? "" : $"; decision={DecisionReason}; stable_ids={Encode(ids)}";
    }

    internal static string ClosureOutput(string note, IEnumerable<ReviewFinding> resolved)
    {
        var findings = resolved.ToArray();
        if (!findings.Any(finding => finding.EvidenceOutcome?.DecisionReason == DecisionReason)) return note;
        // The merged snapshot records closure now; structured fields also preserve it when later
        // Tester submissions replay historical rounds from their authoritative output.
        return note + Environment.NewLine + "findings: " + JsonSerializer.Serialize(findings) +
            Environment.NewLine + "touched_anchors: []";
    }

    private static FailedGoalFindingObservation Unusable(string receiptId, string detail) =>
        FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
            $"Outside-diff rerun receipt {receiptId} is unusable: {detail}. Operator evidence required; no worker was dispatched.");

    private static void RaiseNotice(ConductorDriver driver, Goal goal, string sha, Rerun rerun, FindingEvidenceReceipt receipt)
    {
        var directory = ConductorAttentionStoreDirectory.Resolve(driver._cohortWorkspace, driver._executionDirectory);
        if (directory is null || !Directory.Exists(directory)) return;
        try
        {
            CollaborationItemStore.ForDirectory(directory).RaiseAsync(CollaborationItemType.Notice, goal.Id.Value,
                "Flaky test passed conductor rerun",
                $"tests={string.Join(", ", rerun.Identities.Select(item => item.Identity))}; candidate={sha}; " +
                $"original_receipts={Encode(rerun.Identities.Select(item => item.ReceiptId))}; " +
                $"candidate_receipt={receipt.ReceiptId}; baseline_receipt={receipt.ReceiptId}/Baseline; decision={DecisionReason}",
                $"{DecisionReason}:{goal.Id.Value}:{rerun.Finding.StableId}", CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Outside-diff flaky-test notice failed: receipt_id={receipt.ReceiptId}; {ex}");
        }
    }
}
