using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal LandingEscalationRecheckResult RecheckPreLandingRebaseConflict(Goal goal)
        => _recheckPreLandingRebaseConflict(goal);

    private ConductorAdvanceResult? RebaseOrRetire(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        string phase,
        bool applySideEffects,
        out ConductorParallelAcceptanceEarlyOutcome? earlyOutcome,
        out GoalWorktreeRebaseStatus rebaseStatus)
    {
        earlyOutcome = null;
        var rebase = _rebaseOntoMain(goal);
        rebaseStatus = rebase.Status;

        // No retry here, deliberately. An earlier version of this method retried a non-conflict failure once,
        // on the theory that those failures were transient races against main advancing. That theory was
        // WRONG: the nine occurrences that motivated it were a deterministic git exit 128, because the
        // pre-landing rebase runs inside the hermetic acceptance child, which had no git identity and so
        // could not create the commits a rebase replays. Retrying a deterministic failure cannot help, and it
        // doubled the cost of every genuine one. The identity fix belongs in the environment, not here.
        if (rebase.UpdatedBranch)
        {
            return null;
        }

        if (rebase.Status == GoalWorktreeRebaseStatus.MissingBranch)
        {
            var detail = $"Conductor tick retired missing goal branch before landing because the goal artifact could not be rebased: {rebase.Message}";
            earlyOutcome = ConductorParallelAcceptanceEarlyOutcome.MissingBranchRetired(GoalLifecycleState.CleanedUp, detail);
            if (applySideEffects)
            {
                _recordMissingBranchRetirement(goal, detail);
            }

            return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp));
        }

        var rebaseReason = rebase.Status == GoalWorktreeRebaseStatus.Conflict
            ? $"{phase} rebase conflict ({string.Join(", ", rebase.ConflictFiles)}); use 'workspace rebase' to resolve"
            : $"{phase} rebase failed: {rebase.Message}";
        earlyOutcome = ConductorParallelAcceptanceEarlyOutcome.PreLandingEscalated(GoalLifecycleState.Verified, rebaseReason);
        return applySideEffects
            ? Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, rebaseReason)
            : MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, rebaseReason));
    }

    private ConductorAdvanceResult CompleteLandingAfterAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        // Census the failing tests at the handler's single entry so every gate completion is recorded,
        // including the apparatus and unattributable early returns below. Lazy keeps the changed-path
        // git call at exactly one per advance and off gate completions that never need it.
        var landingFileScopes = new Lazy<IReadOnlyList<string>>(() => _getLandingFileScopes(goal));
        var apparatusRedReading = _apparatusRedGate?.RecordGateCompletion(goal, acceptance, landingFileScopes);
        if (TryDisposeWithinAttemptRerunApparatus(goal, goalPrefix, policy, acceptance) is { } rerunApparatus)
        {
            return rerunApparatus;
        }
        if (IsEnvironmentalApparatusAcceptanceRun(acceptance))
        {
            var failedChecks = acceptance.FailedChecks is { Count: > 0 }
                ? acceptance.FailedChecks
                : acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray();
            var observedHeads = _resolveAcceptanceHeads(goal);
            var branchHeadSha = acceptance.BranchHeadSha ?? observedHeads.BranchHeadSha;
            var mainHeadSha = acceptance.MainHeadSha ?? observedHeads.MainHeadSha;
            _recordAcceptanceFailure(
                goal,
                failedChecks,
                branchHeadSha,
                mainHeadSha,
                acceptance.CheckAttributions,
                acceptance.BaselineAttestation);
            var reason =
                $"Acceptance gate apparatus/environmental failure recorded for unchanged candidate " +
                $"{FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)}. Acceptance will not re-run until " +
                "the candidate or main HEAD changes, or an operator confirms acceptance-retry; no worker was reopened.";
            RecordEscalation(goal, GoalLifecycleState.Verified, reason);
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    reason,
                    StableIdentity: $"acceptance-apparatus:{branchHeadSha ?? "unknown"}:{mainHeadSha ?? "unknown"}"));
        }

        if (!acceptance.Passed && acceptance.RequiredUnmetCriteria.Count == 0)
        {
            var failedChecks = acceptance.FailedChecks ?? [];
            var timedOut = failedChecks.Any(IsBlockingTimeoutCheck);
            if (!timedOut && acceptance.FailedChecks is { Count: > 0 })
            {
                _recordAcceptanceFailure(
                    goal,
                    failedChecks,
                    acceptance.BranchHeadSha,
                    acceptance.MainHeadSha,
                    acceptance.CheckAttributions,
                    acceptance.BaselineAttestation);
            }

            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                (timedOut
                    ? "Acceptance verification timed out; rerun acceptance after clearing the blocker."
                    : "Acceptance verification failed; review and fix before landing.") +
                FormatFailureTail(acceptance.FailureDetail), timedOut ? null : ConductorEscalationKind.AcceptanceVerificationFailed);
        }

        if (acceptance.Passed)
        {
            goal = GetCurrentGoal(goal);
            _clearAcceptanceFailure(goal);
            var evidenceCandidateSha = acceptance.BranchHeadSha ?? _resolveAcceptanceHeads(goal).BranchHeadSha;
            var evidenceHold = AcceptanceCriterionEvidence.RecordAndCreateHold(goal, evidenceCandidateSha, _cohortKernel ?? _conductorTickKernel, _executionDirectory);
            if (evidenceHold is not null)
            {
                return MakeResult(goal.Id.Value, goalPrefix, policy, evidenceHold);
            }
        }

        if (acceptance.RequiredUnmetCriteria.Count > 0)
        {
            if (TryHoldForOwnerReview(goal, goalPrefix, policy, acceptance) is { } ownerReviewHold)
                return ownerReviewHold;

            var retryDisposition = ClassifyAcceptanceRetry(acceptance.RequiredUnmetCriteria);
            var attemptedAllFlakyDisposition = false;
            if (retryDisposition.ActionableCriteria.Count == 0 &&
                TryDisposeExcludedAcceptanceFailures(
                    goal, goalPrefix, policy, acceptance, apparatusRedReading,
                    ref retryDisposition, out attemptedAllFlakyDisposition) is { } excludedResult)
            {
                return excludedResult;
            }

            if (!attemptedAllFlakyDisposition &&
                TryDisposeApparatusRed(goal, goalPrefix, policy, acceptance, apparatusRedReading) is { } apparatusRed)
            {
                return apparatusRed;
            }

            if (retryDisposition.ExcludedFailures.Count > 0)
            {
                var observedHeads = _resolveAcceptanceHeads(goal);
                _recordAcceptanceFailure(
                    goal,
                    acceptance.FailedChecks is { Count: > 0 }
                        ? acceptance.FailedChecks
                        : acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray(),
                    acceptance.BranchHeadSha ?? observedHeads.BranchHeadSha,
                    acceptance.MainHeadSha ?? observedHeads.MainHeadSha,
                    acceptance.CheckAttributions,
                    acceptance.BaselineAttestation);
            }

            var criteria = FormatUnmetCriteria(retryDisposition.ActionableCriteria);
            var task = SelectTaskForCriterionRetry(goal);
            if (task is null)
            {
                return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                    $"Acceptance criteria unmet but no completed task is available to retry: {criteria}; review/land manually");
            }

            if (goal.AutomaticAcceptanceRetryCount < policy.MaxCriterionRetries)
            {
                var retryFeedback = FormatCriterionRetryFeedback(retryDisposition.ActionableCriteria);
                var retryCount = _recordCriterionRetryFeedback(
                    goal.Id,
                    task.Id,
                    retryFeedback);
                var retryMessage = $"Acceptance criteria unmet; retrying task with feedback (attempt {retryCount}/{policy.MaxCriterionRetries}): " +
                    string.Join(Environment.NewLine, retryFeedback);
                _retryTask(goal.Id, task.Id, retryMessage, null, RetryCause.CriterionEvidenceOwnerMismatch);
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, retryMessage));
            }

            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                $"Acceptance criteria unmet after {goal.AutomaticAcceptanceRetryCount} retries: {criteria}; review/land manually");
        }

        foreach (var task in goal.Tasks)
        {
            _clearCriterionRetryFeedback(goal.Id, task.Id);
        }

        // Gate 3: land via integration branch (the branch is already rebased onto main by Gate 1).
        var mutationBlockReason = LandingMutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(mutationBlockReason))
        {
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Landing held at mutation boundary: {mutationBlockReason}"));
        }

        var landResult = _land(goal, policy);
        if (landResult.Decision is LandingDecision.Escalate escalate)
        {
            if (LandingExecutor.IsMutationHoldEscalation(escalate.Reason))
            {
                return MakeResult(
                    goal.Id.Value,
                    goalPrefix,
                    policy,
                    new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, escalate.Reason));
            }

            if (LandingExecutor.IsOwnershipHoldEscalation(escalate.Reason))
            {
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, escalate.Reason));
            }

            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, escalate.Reason);
        }

        RecordAdvisoryAcceptanceNotes(goal, acceptance.AdvisoryUnmetCriteria);

        if (landResult.MainAdvanced)
        {
            // Schedule any required generation handoff before fallible advisory/post-landing work.
            // Main has already advanced, so losing this receipt would permanently miss the relaunch.
            SuccessfulLandingSink?.Invoke(new ConductorLandingReceipt(
                goal.Id.Value,
                landResult.ChangedFiles ?? landingFileScopes.Value,
                landResult.MergeCommitSha));

            // Gate 4: advisory semantic acceptance runs only after deterministic acceptance and
            // successful landing. It records judge receipts for observability but never gates landing.
            _runAdvisorySemanticAcceptance(goal, acceptance);
            _afterSuccessfulLanding(goal, landResult);
        }

        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, $"Landed: {landResult.Message}"));
    }

    private ConductorAdvanceResult ExecuteRecord(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        _record(goal);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Merged, "Recorded to dogfood log"));
    }

    private ConductorAdvanceResult ExecuteCleanup(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        GoalWorktreeRemoveResult cleanup;
        try
        {
            cleanup = _cleanup(goal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _completeGoal(goal);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Executed(
                    GoalLifecycleState.Recorded,
                    $"Workspace cleanup deferred after removal failure; retry later. {ex.Message}"));
        }

        _completeGoal(goal);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Recorded, cleanup.Message));
    }

    private ConductorAdvanceResult Escalate(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState state,
        string reason, ConductorEscalationKind? kind = null)
    {
        RecordEscalation(goal, state, reason);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Escalated(state, reason, kind));
    }

    private void RecordEscalation(Goal goal, GoalLifecycleState state, string reason)
    {
        var escalationClock = Stopwatch.StartNew();
        var sinkResult = LandingEscalationWriteResult.Error;
        try
        {
            sinkResult = _writeEscalation(goal, state, reason);
        }
        finally
        {
            escalationClock.Stop();
            EmitGoalPhaseTiming(
                "escalation-write",
                goal,
                escalationClock.Elapsed,
                $"json_ms={sinkResult.JsonElapsedMilliseconds} json={sinkResult.JsonOutcome} " +
                $"collab_ms={sinkResult.CollaborationElapsedMilliseconds} collab={sinkResult.CollaborationOutcome} " +
                $"channel_ms={sinkResult.ChannelElapsedMilliseconds} channel={sinkResult.ChannelOutcome}");
        }
    }

    private static ConductorAdvanceResult MakeResult(
        string goalId,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        ConductorAdvanceOutcome outcome) =>
        new(goalId, goalPrefix, policy.Name, outcome);

    private static TaskSpec? SelectTaskForCriterionRetry(Goal goal) =>
        goal.Tasks.LastOrDefault(task => task.Status == WorkTaskStatus.Completed && task.RequiredRole == AgentRole.Developer) ??
        goal.Tasks.LastOrDefault(task => task.Status == WorkTaskStatus.Completed);

    private void RecordAdvisoryAcceptanceNotes(Goal goal, IReadOnlyList<AcceptanceCheckResult> advisoryCriteria)
    {
        if (advisoryCriteria.Count == 0)
        {
            return;
        }

        var task = SelectTaskForCriterionRetry(goal);
        if (task is null)
        {
            return;
        }

        _recordTaskNote(
            goal.Id,
            task.Id,
            $"Advisory acceptance criteria observed during landing (non-gating): {FormatUnmetCriteria(advisoryCriteria)}");
    }

    private static string FormatUnmetCriteria(IReadOnlyList<AcceptanceCheckResult> criteria) =>
        string.Join("; ", criteria.Select(FormatUnmetCriterion));

    private static string FormatCleanupDiagnostic(GoalWorktreeRemoveResult cleanup)
    {
        var parts = new List<string> { cleanup.Message };
        if (!string.IsNullOrWhiteSpace(cleanup.LeftoverPath))
        {
            parts.Add($"leftover={cleanup.LeftoverPath}");
        }

        if (cleanup.LockHolders.Count > 0)
        {
            parts.Add("lockHolders=" + string.Join(", ", cleanup.LockHolders.Select(FormatLockHolder)));
        }

        if (cleanup.CleanupBackoff is not null)
        {
            parts.Add(GoalWorktrees.FormatCleanupBackoff(cleanup.CleanupBackoff));
        }

        if (!string.IsNullOrWhiteSpace(cleanup.ResumeCommand))
        {
            parts.Add($"resume={cleanup.ResumeCommand}");
        }

        return string.Join(" ", parts);
    }

    private static string[] InferRecordedFileScopes(Goal goal)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in goal.Tasks)
        {
            var text = $"{goal.Objective}\n{task.Description}\n{task.VerificationPlan}";
            foreach (var token in text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = token.Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']');
                if (normalized.Contains('/') && !string.IsNullOrWhiteSpace(Path.GetExtension(normalized)))
                {
                    scopes.Add(normalized.TrimStart('/'));
                }
            }
        }

        return scopes.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string FormatLockHolder(WorktreeLockHolder holder) =>
        string.IsNullOrWhiteSpace(holder.CommandLine)
            ? $"pid={holder.ProcessId} name={holder.ProcessName}"
            : $"pid={holder.ProcessId} name={holder.ProcessName} command=\"{holder.CommandLine}\"";

    private static AcceptanceRetryDisposition ClassifyAcceptanceRetry(
        IReadOnlyList<AcceptanceCheckResult> criteria)
    {
        var actionable = new List<AcceptanceCheckResult>();
        var excluded = new List<ExcludedAcceptanceFailure>();
        foreach (var criterion in criteria)
        {
            var failingIdentities = criterion.FailingTestIdentities?
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (failingIdentities is not { Length: > 0 })
            {
                // Check-level same-name co-failure is not proof that this exact failure existed at the
                // merge base. Identity-less failures therefore remain actionable.
                actionable.Add(criterion);
                continue;
            }

            var actionableIdentities = new List<string>();
            if (criterion.FailingTestAttributions is { Count: > 0 } testAttributions &&
                failingIdentities is { Length: > 0 })
            {
                var byIdentity = testAttributions
                    .GroupBy(attribution => attribution.TestIdentity, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (var identity in failingIdentities)
                {
                    if (byIdentity.TryGetValue(identity, out var attribution) &&
                        (attribution.Origin == AcceptanceTestFailureOrigin.Inherited ||
                         (attribution.Origin == AcceptanceTestFailureOrigin.UnconfirmedIntroduced &&
                          attribution.CandidateRerun?.Outcome == "Passed" &&
                          !string.IsNullOrWhiteSpace(attribution.CandidateRerun.ReceiptPointer))))
                    {
                        excluded.Add(new ExcludedAcceptanceFailure(
                            identity,
                            attribution.Origin == AcceptanceTestFailureOrigin.Inherited
                                ? AcceptanceRetryExclusionKind.Inherited
                                : AcceptanceRetryExclusionKind.UnconfirmedIntroduced,
                            attribution.CandidateRerun?.ReceiptPointer));
                    }
                    else
                    {
                        actionableIdentities.Add(identity);
                    }
                }
            }
            else
            {
                // Only per-identity merge-base attribution can suppress a retry. Check-level baseline
                // co-failure does not establish that these identities are inherited.
                actionableIdentities.AddRange(failingIdentities);
            }

            if (actionableIdentities.Count > 0)
            {
                actionable.Add(criterion with { FailingTestIdentities = actionableIdentities });
            }
        }

        return new AcceptanceRetryDisposition(actionable, excluded);
    }

    private static string BuildUnattributableAcceptanceIdentity(
        string? branchHeadSha,
        string? mainHeadSha,
        IReadOnlyList<ExcludedAcceptanceFailure> excludedFailures)
    {
        var fingerprintSource = string.Join(
            "|",
            excludedFailures
                .OrderBy(failure => failure.Identity, StringComparer.Ordinal)
                .ThenBy(failure => failure.Kind)
                .Select(failure => $"{failure.Kind}:{failure.Identity}"));
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant()[..16];
        return $"acceptance-unattributable:{branchHeadSha ?? "unknown"}:{mainHeadSha ?? "unknown"}:{fingerprint}";
    }

    private static string[] FormatCriterionRetryFeedback(IReadOnlyList<AcceptanceCheckResult> criteria)
    {
        var concreteEvidence = ExtractConcreteRetryEvidence(criteria);
        if (concreteEvidence.Count == 0)
        {
            return criteria.Select(FormatUnmetCriterion).ToArray();
        }

        var feedback = new List<string>
        {
            "Concrete acceptance failure evidence:",
        };
        feedback.AddRange(concreteEvidence);
        feedback.Add("Acceptance criteria summary:");
        feedback.AddRange(criteria.Select(FormatUnmetCriterion));
        return feedback.ToArray();
    }

    private static List<string> ExtractConcreteRetryEvidence(IReadOnlyList<AcceptanceCheckResult> criteria)
    {
        var evidence = new List<string>();
        var remainingEvidenceEntries = MaxCriterionRetryEvidenceLines;
        foreach (var criterion in criteria.Where(criterion => !criterion.Passed))
        {
            var outputEvidence = ExtractConcreteOutputEvidence(criterion).ToArray();
            var allowedFailingIdentities = criterion.FailingTestIdentities?.
                Where(identity => !string.IsNullOrWhiteSpace(identity))
                .ToHashSet(StringComparer.Ordinal);
            var testResultPaths = criterion.TestResultPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];

            if (outputEvidence.Length == 0 && testResultPaths.Length == 0)
            {
                continue;
            }

            evidence.Add(FormatFailedCheckEvidence(criterion));

            if (testResultPaths.Length == 0)
            {
                AddBoundedOutputEvidence(evidence, outputEvidence, ref remainingEvidenceEntries);
                evidence.Add($"detail unavailable: no TRX path was recorded for partition \"{criterion.Name}\"");
                continue;
            }

            var fallbackAdded = false;
            foreach (var testResultPath in testResultPaths)
            {
                var receipt = AcceptanceTrxFailureReader.Read(testResultPath);
                if (receipt.Status != AcceptanceTrxReadStatus.Readable)
                {
                    if (!fallbackAdded)
                    {
                        AddBoundedOutputEvidence(evidence, outputEvidence, ref remainingEvidenceEntries);
                        fallbackAdded = true;
                    }

                    var detail = string.IsNullOrWhiteSpace(receipt.Detail) ? string.Empty : $" ({receipt.Detail})";
                    evidence.Add(
                        $"detail unavailable: {DescribeTrxReadFailure(receipt.Status)} for partition \"{criterion.Name}\" at {receipt.Path}{detail}");
                    continue;
                }

                evidence.Add($"TRX receipt for partition \"{criterion.Name}\": {receipt.Path}");
                if (receipt.Failures.Count == 0)
                {
                    if (!fallbackAdded)
                    {
                        AddBoundedOutputEvidence(evidence, outputEvidence, ref remainingEvidenceEntries);
                        fallbackAdded = true;
                    }

                    evidence.Add(
                        $"detail unavailable: readable TRX for partition \"{criterion.Name}\" contained no non-passing results at {receipt.Path}");
                    continue;
                }

                var attributableFailures = allowedFailingIdentities is null or { Count: 0 }
                    ? receipt.Failures
                    : receipt.Failures
                        .Where(failure => allowedFailingIdentities.Contains(failure.TestName))
                        .ToArray();
                if (attributableFailures.Count == 0)
                {
                    if (!fallbackAdded)
                    {
                        AddBoundedOutputEvidence(evidence, outputEvidence, ref remainingEvidenceEntries);
                        fallbackAdded = true;
                    }

                    evidence.Add(
                        $"detail unavailable: readable TRX for partition \"{criterion.Name}\" contained no attributable non-passing results at {receipt.Path}");
                    continue;
                }

                var omittedFailures = 0;
                foreach (var failure in attributableFailures)
                {
                    if (string.IsNullOrWhiteSpace(failure.TestName))
                    {
                        evidence.Add(
                            $"detail unavailable: test identity unavailable for outcome \"{failure.Outcome}\" in partition \"{criterion.Name}\" at {receipt.Path}");
                        continue;
                    }

                    if (remainingEvidenceEntries == 0)
                    {
                        omittedFailures++;
                        continue;
                    }

                    evidence.Add(FormatTrxFailureEvidence(failure));
                    remainingEvidenceEntries--;
                }

                if (omittedFailures > 0)
                {
                    evidence.Add($"{omittedFailures} more failures omitted — see {receipt.Path}.");
                }
            }
            AppendAssemblyCleanupStderrEvidence(evidence, criterion, testResultPaths);
        }

        return evidence;
    }

    private static void AddBoundedOutputEvidence(
        ICollection<string> evidence,
        IReadOnlyList<string> outputEvidence,
        ref int remainingEvidenceEntries)
    {
        var retainedCount = Math.Min(outputEvidence.Count, remainingEvidenceEntries);
        for (var index = 0; index < retainedCount; index++)
        {
            evidence.Add(outputEvidence[index]);
        }

        remainingEvidenceEntries -= retainedCount;
        if (retainedCount < outputEvidence.Count)
        {
            evidence.Add($"{outputEvidence.Count - retainedCount} more acceptance evidence entries omitted.");
        }
    }

    private static string FormatTrxFailureEvidence(AcceptanceTrxFailure failure)
    {
        var message = string.IsNullOrEmpty(failure.Message) ? "failure message unavailable" : failure.Message;
        var stackTrace = string.IsNullOrEmpty(failure.StackTrace) ? "stack trace unavailable" : failure.StackTrace;
        return $"[FAIL] {failure.TestName} ({failure.Outcome}){Environment.NewLine}{message}{Environment.NewLine}{stackTrace}";
    }

    private static string DescribeTrxReadFailure(AcceptanceTrxReadStatus status) => status switch
    {
        AcceptanceTrxReadStatus.Missing => "no TRX exists",
        AcceptanceTrxReadStatus.Unparseable => "TRX is unparseable",
        AcceptanceTrxReadStatus.Unreadable => "TRX is unreadable",
        _ => "no readable TRX"
    };

    private static string FormatFailedCheckEvidence(AcceptanceCheckResult criterion) =>
        $"failed check: {criterion.Name} (exit code {criterion.ExitCode})";

    private static IEnumerable<string> ExtractConcreteOutputEvidence(AcceptanceCheckResult criterion)
    {
        foreach (var line in SplitEvidenceLines(criterion.OutputTail))
        {
            if (AcceptanceRetryEvidencePattern.IsMatch(line))
            {
                yield return line;
            }
        }
    }

    private static string FormatPreReviewBuildDiagnostic(IReadOnlyList<AcceptanceCheckResult> checks)
    {
        var failedChecks = checks.Where(check => !check.Passed).ToArray();
        var lines = failedChecks
            .Where(check => !string.IsNullOrWhiteSpace(check.OutputTail))
            .SelectMany(ExtractConcreteOutputEvidence)
            .Take(5)
            .ToArray();
        lines = lines.Length > 0 ? lines : failedChecks
            .Select(check => check.ResultSummary)
            .Concat(failedChecks.Select(check => check.OutputTail))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .SelectMany(text => SplitEvidenceLines(text!))
            .Take(5)
            .ToArray();
        var diagnostic = lines.Length == 0 ? "no diagnostic output was captured" : string.Join(" | ", lines);
        return diagnostic.Length <= 1000 ? diagnostic : $"{diagnostic[..997]}...";
    }

    private static IEnumerable<string> SplitEvidenceLines(string? text) =>
        (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line));

    private static string FormatUnmetCriterion(AcceptanceCheckResult criterion)
    {
        var summary = string.IsNullOrWhiteSpace(criterion.ResultSummary)
            ? criterion.OutputTail
            : criterion.ResultSummary;
        return string.IsNullOrWhiteSpace(summary)
            ? criterion.Name
            : $"{criterion.Name}: {summary.Trim()}";
    }
}
