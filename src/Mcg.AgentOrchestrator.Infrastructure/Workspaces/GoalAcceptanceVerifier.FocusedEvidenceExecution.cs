using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceExecution
{
    internal delegate Task<FocusedEvidenceArmRunResult> FocusedEvidenceArmRunner(
        FindingEvidenceArm arm,
        string sha,
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        FocusedEvidenceCoverage coverage,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetBuildEnvironment? executionEnvironment,
        CancellationToken cancellationToken,
        bool continueAfterFailure = false,
        IAcceptanceRunExecutionContext? executionOwner = null,
        bool armContextApplied = false);

    internal delegate Task<FocusedEvidenceArmRunResult> FocusedEvidenceBaselineArmRunner(
        string candidateWorktreePath,
        string? baselineSha,
        GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        FocusedEvidenceCoverage coverage,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        bool classifyMissingSelectionsAsAbsent = false,
        IAcceptanceRunExecutionContext? executionOwner = null,
        bool partitionCandidateOnlySelections = false);

    internal delegate Task<FocusedEvidenceRunResult> SourceRevertedEvidenceAppender(
        FocusedEvidenceRunResult evidence, FindingEvidenceNegativeControl? mode,
        string worktreePath, GoalId? goalId, IReadOnlyList<AcceptanceManifestCheck> checks,
        FocusedEvidenceCoverage coverage, int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease, IAcceptanceRunExecutionContext executionOwner,
        string? mergeBase = null, IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null, IReadOnlyList<string>? declaredPaths = null);

    internal static async Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
        Func<string, GoalId?, string, IAcceptanceFocusedVerificationOwner, FindingEvidenceNegativeControl, int?, DotnetBuildEnvironmentLease?, bool, IReadOnlyList<string>?, FindingEvidenceMutation?, IReadOnlyList<string>?, Task<FocusedEvidenceRunResult>> runNegativeControlFocusedEvidenceOwned,
        Func<string, GoalId?, string, IAcceptanceFocusedVerificationOwner, int?, DotnetBuildEnvironmentLease?, bool, Task<FocusedEvidenceRunResult>> runFocusedEvidenceOwned,
        string worktreePath,
        GoalId? goalId,
        string request,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false,
        CancellationToken cancellationToken = default,
        FindingEvidenceNegativeControl? negativeControl = null, IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null, IReadOnlyList<string>? declaredPaths = null)
    {
        if (mutation is not null && negativeControl != FindingEvidenceNegativeControl.RevertSrc)
            throw new ArgumentException("mutation requires negative_control 'revert-src'");
        var executionOwner = AcceptanceExecutionOwners.CreateFocusedVerification(
            worktreePath, goalId, stableSlotIndex, cancellationToken);
        await using (executionOwner.ConfigureAwait(false))
        {
            if (negativeControl is { } mode)
                return await runNegativeControlFocusedEvidenceOwned(worktreePath, goalId, request,
                    executionOwner, mode, stableSlotIndex, stableSlotLease, runBaselineArm, revertPaths, mutation, declaredPaths).ConfigureAwait(false);
            return await runFocusedEvidenceOwned(
                worktreePath, goalId, request, executionOwner, stableSlotIndex,
                stableSlotLease, runBaselineArm).ConfigureAwait(false);
        }
    }

    internal static Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
        Action ensureTestOverridesUnchanged,
        Func<AcceptanceFocusedVerificationOwner, string, GoalId?, string, int?, DotnetBuildEnvironmentLease?, bool, Task<FocusedEvidenceRunResult>> executeFocusedVerification,
        string worktreePath,
        GoalId? goalId,
        string request,
        IAcceptanceFocusedVerificationOwner executionOwner,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false)
    {
        ensureTestOverridesUnchanged();
        return executionOwner is AcceptanceFocusedVerificationOwner owner
            ? executeFocusedVerification(owner, worktreePath, goalId, request, stableSlotIndex, stableSlotLease, runBaselineArm)
            : throw new ArgumentException("A focused-verification execution owner is required.", nameof(executionOwner));
    }

    internal static async Task<FocusedEvidenceRunResult> RunOwnedFocusedEvidenceAsync(
        IAcceptanceRunExecutionContext? executionContext,
        FocusedEvidenceArmRunner runFocusedEvidenceArm,
        FocusedEvidenceBaselineArmRunner runBaselineFocusedEvidenceArm,
        SourceRevertedEvidenceAppender addSourceRevertedEvidence,
        Func<string, string?> resolveFocusedEvidenceMergeBase,
        string worktreePath,
        GoalId? goalId,
        string request,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false,
        CancellationToken cancellationToken = default,
        FindingEvidenceNegativeControl? negativeControl = null, IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null, IReadOnlyList<string>? declaredPaths = null)
    {
        var executionOwner = (AcceptanceFocusedVerificationOwner)(executionContext ?? throw new InvalidOperationException(
            "Focused verification execution context was not supplied."));
        var engineSettings = executionOwner.Settings;

        if (!FocusedEvidenceRequestResolver.TryBuildFocusedEvidenceChecks(
                request,
                engineSettings,
                worktreePath,
                out var focusedChecks,
                out var coverage,
                out var rejection))
        {
            return new FocusedEvidenceRunResult(
                request,
                Accepted: false,
                Passed: false,
                Summary: rejection.Detail,
                Checks: [],
                Rejection: rejection);
        }

        var candidateSha = GoalAcceptanceVerifier.ResolveGitScalar(worktreePath, "rev-parse", "HEAD") ?? "unavailable";
        FocusedEvidenceArmRunResult candidate;
        if (runBaselineArm)
        {
            EmitFocusedEvidenceArmStarted(FindingEvidenceArm.Candidate, candidateSha);
        }

        try
        {
            candidate = await runFocusedEvidenceArm(
                FindingEvidenceArm.Candidate,
                candidateSha,
                worktreePath,
                goalId,
                focusedChecks,
                coverage,
                stableSlotIndex,
                stableSlotLease,
                executionEnvironment: null,
                cancellationToken: executionOwner.CancellationToken,
                executionOwner: executionOwner).ConfigureAwait(false);
        }
        catch (Exception ex) when (runBaselineArm)
        {
            EmitFocusedEvidenceArmFailed(FindingEvidenceArm.Candidate, candidateSha, ex);
            throw;
        }

        if (runBaselineArm)
        {
            EmitFocusedEvidenceArmResolved(candidate);
        }

        if (!runBaselineArm)
        {
            if (candidate.Disposition == FindingEvidenceArmDisposition.Green)
            {
                executionOwner.MarkSuccessful();
            }

            return await addSourceRevertedEvidence(new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: candidate.Disposition == FindingEvidenceArmDisposition.Green,
                Summary: candidate.Summary,
                Checks: candidate.Checks,
                Coverage: coverage,
                Arms: [candidate],
                OutcomeReason: candidate.Disposition == FindingEvidenceArmDisposition.ApparatusFailure
                    ? FindingEvidenceOutcomeReason.ApparatusFailure
                    : null), negativeControl, worktreePath, goalId, focusedChecks, coverage,
                stableSlotIndex, stableSlotLease, executionOwner, revertPaths: revertPaths, mutation: mutation, declaredPaths: declaredPaths).ConfigureAwait(false);
        }

        var baselineSha = resolveFocusedEvidenceMergeBase(worktreePath);
        EmitFocusedEvidenceArmStarted(FindingEvidenceArm.Baseline, baselineSha ?? "unavailable");
        FocusedEvidenceArmRunResult baseline;
        try
        {
            baseline = await runBaselineFocusedEvidenceArm(
                worktreePath,
                baselineSha,
                goalId,
                focusedChecks,
                coverage,
                stableSlotIndex,
                stableSlotLease,
                executionOwner.CancellationToken,
                executionOwner: executionOwner,
                partitionCandidateOnlySelections: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmitFocusedEvidenceArmFailed(FindingEvidenceArm.Baseline, baselineSha ?? "unavailable", ex);
            throw;
        }

        EmitFocusedEvidenceArmResolved(baseline);
        var outcomeReason = ClassifyFocusedEvidenceExperiment(candidate, baseline);
        EmitFocusedEvidenceClassification(candidate, baseline, outcomeReason);
        var summary =
            $"focused evidence {FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(outcomeReason)}; " +
            $"candidate={ArmDispositionWireValue(candidate.Disposition)} ({candidate.Summary}); " +
            $"baseline={ArmDispositionWireValue(baseline.Disposition)} ({baseline.Summary})";
        var evidence = new FocusedEvidenceRunResult(
            request,
            Accepted: true,
            Passed: candidate.Disposition == FindingEvidenceArmDisposition.Green,
            Summary: summary,
            Checks: candidate.Checks,
            Coverage: coverage,
            Arms: [candidate, baseline],
            OutcomeReason: outcomeReason);
        if (candidate.Disposition == FindingEvidenceArmDisposition.Green)
        {
            executionOwner.MarkSuccessful();
        }

        return await addSourceRevertedEvidence(evidence, negativeControl, worktreePath, goalId,
            focusedChecks, coverage, stableSlotIndex, stableSlotLease, executionOwner, baselineSha, revertPaths, mutation, declaredPaths).ConfigureAwait(false);
    }

    internal static async Task<FocusedEvidenceArmRunResult> RunFocusedEvidenceArmAsync(
        Func<IAcceptanceRunExecutionContext, FocusedEvidenceArmRunner> scopeFocusedEvidenceArm,
        Func<string, IReadOnlyList<AcceptanceManifestCheck>, GoalId?, int?, DotnetBuildEnvironmentLease?, DotnetBuildEnvironment?, CancellationToken, bool, IAcceptanceRunExecutionContext?, Task<IReadOnlyList<AcceptanceCheckResult>>> runFocusedEvidenceChecks,
        Func<AcceptanceCheckResult, string> captureLimitDetail,
        FindingEvidenceArm arm,
        string sha,
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        FocusedEvidenceCoverage coverage,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetBuildEnvironment? executionEnvironment,
        CancellationToken cancellationToken,
        bool continueAfterFailure = false,
        IAcceptanceRunExecutionContext? executionOwner = null,
        bool armContextApplied = false)
    {
        if (!armContextApplied && executionOwner is not null)
        {
            var armContext = new AcceptanceRunExecutionContextView(
                executionOwner,
                $"{executionOwner.ResultsPrefix}-{arm.ToString().ToLowerInvariant()}");
            return await scopeFocusedEvidenceArm(armContext)(
                arm,
                sha,
                worktreePath,
                goalId,
                focusedChecks,
                coverage,
                stableSlotIndex,
                stableSlotLease,
                executionEnvironment,
                cancellationToken,
                continueAfterFailure,
                armContext,
                armContextApplied: true).ConfigureAwait(false);
        }

        var checks = await runFocusedEvidenceChecks(
            worktreePath, focusedChecks, goalId, stableSlotIndex, stableSlotLease,
            executionEnvironment, cancellationToken, continueAfterFailure, executionOwner).ConfigureAwait(false);
        var disposition = ClassifyFocusedEvidenceArm(checks);
        var failed = checks.FirstOrDefault(check => !check.Passed);
        var receiptPaths = checks
            .SelectMany(check => check.TestResultPaths ?? [])
            .Concat(checks.Select(check => check.ArtifactsPath ?? string.Empty))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var planSummary = GoalAcceptanceVerifier.FormatFocusedEvidencePlanSummary(coverage);
        var summary = failed is null
            ? $"{checks.Count} check(s) passed; {planSummary}; receipts: {GoalAcceptanceVerifier.FormatReceiptPaths(receiptPaths)}"
            : disposition == FindingEvidenceArmDisposition.ApparatusFailure
                ? $"focused selection apparatus failure: {failed.Name}{captureLimitDetail(failed)}; {planSummary}; receipts: {GoalAcceptanceVerifier.FormatReceiptPaths(receiptPaths)}"
            : $"{failed.Name}{captureLimitDetail(failed)} exit {failed.ExitCode}; {planSummary}; receipts: {GoalAcceptanceVerifier.FormatReceiptPaths(receiptPaths)}";
        return new FocusedEvidenceArmRunResult(
            arm,
            sha,
            disposition,
            Accepted: true,
            Passed: disposition == FindingEvidenceArmDisposition.Green,
            summary,
            checks);
    }

    internal static async Task<FocusedEvidenceArmRunResult> RunBaselineFocusedEvidenceArmAsync(
        FocusedEvidenceArmRunner runFocusedEvidenceArm,
        Func<string> getFocusedEvidenceBaselineRoot,
        Func<string, string?> tryLabelFocusedEvidenceBaselineWorktree,
        Func<IReadOnlyList<AcceptanceManifestCheck>, string, string, bool, bool, AcceptanceFailureAttributionPlanner.BaselineSourceSelectionPlan> selectBaselineFocusedChecks,
        Func<FocusedEvidenceArmRunResult, IReadOnlyList<AcceptanceCheckResult>, FocusedEvidenceArmRunResult> combineFindingBaselineArm,
        DotnetBuildStorageRoot storageRoot,
        string candidateWorktreePath,
        string? baselineSha,
        GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        FocusedEvidenceCoverage coverage,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        bool classifyMissingSelectionsAsAbsent = false,
        IAcceptanceRunExecutionContext? executionOwner = null,
        bool partitionCandidateOnlySelections = false)
    {
        if (string.IsNullOrWhiteSpace(baselineSha))
        {
            return InconclusiveBaseline("baseline merge-base could not be resolved");
        }

        var baselineRoot = getFocusedEvidenceBaselineRoot();
        Directory.CreateDirectory(baselineRoot);
        var baselinePath = Path.Combine(
            baselineRoot,
            $"{(goalId?.Value ?? "operator")[..Math.Min(8, (goalId?.Value ?? "operator").Length)]}-{Guid.NewGuid():N}");
        var add = GitCli.Run(
            candidateWorktreePath,
            "worktree", "add", "--detach", baselinePath, baselineSha);
        if (!add.Succeeded)
        {
            TryDeleteFocusedEvidenceBaselineDirectory(baselineRoot, baselinePath);
            return InconclusiveBaseline(
                $"baseline worktree could not be created: {GoalAcceptanceVerifier.TrimForReceipt(add.Error)}",
                baselineSha);
        }

        DotnetBuildEnvironment? baselineEnvironment = null;
        GoalId? baselineEnvironmentId = null;
        try
        {
            if (tryLabelFocusedEvidenceBaselineWorktree(baselinePath) is { } integrityFailure)
            {
                return InconclusiveBaseline(integrityFailure, baselineSha);
            }

            var sourcePlan = selectBaselineFocusedChecks(
                focusedChecks, baselineSha, baselinePath,
                classifyMissingSelectionsAsAbsent, partitionCandidateOnlySelections);

            // The baseline owns a fresh artifact environment. Sharing candidate artifacts could make
            // a structurally broken baseline look like a meaningful RED arm.
            FocusedEvidenceArmRunResult executedArm;
            if (sourcePlan.ExecutableChecks.Count == 0)
            {
                executedArm = new FocusedEvidenceArmRunResult(
                    FindingEvidenceArm.Baseline,
                    baselineSha,
                    FindingEvidenceArmDisposition.Inconclusive,
                    Accepted: true,
                    Passed: false,
                    "no focused baseline checks required execution",
                    []);
            }
            else
            {
                baselineEnvironmentId = GoalId.New();
                baselineEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(
                    baselineEnvironmentId,
                    $"focused-evidence-baseline-{baselineSha[..Math.Min(8, baselineSha.Length)]}", storageRoot: storageRoot);
                executedArm = await runFocusedEvidenceArm(
                    FindingEvidenceArm.Baseline,
                    baselineSha,
                    baselinePath,
                    // Ownerless execution gets an invocation-local build environment. Passing the goal
                    // id here would reuse candidate artifacts and invalidate the negative control.
                    null,
                    sourcePlan.ExecutableChecks,
                    coverage,
                    stableSlotIndex,
                    stableSlotLease,
                    baselineEnvironment,
                    cancellationToken: cancellationToken,
                    continueAfterFailure: classifyMissingSelectionsAsAbsent,
                    executionOwner: executionOwner).ConfigureAwait(false);
            }

            return partitionCandidateOnlySelections
                ? combineFindingBaselineArm(executedArm, sourcePlan.SourceClassificationChecks)
                : AcceptanceFailureAttributionPlanner.CombineBaselineArm(
                    focusedChecks, executedArm, sourcePlan.SourceClassificationChecks,
                    ClassifyFocusedEvidenceArm);
        }
        finally
        {
            _ = GitCli.Run(candidateWorktreePath, "worktree", "remove", "--force", baselinePath);
            TryDeleteFocusedEvidenceBaselineDirectory(baselineRoot, baselinePath);
            if (baselineEnvironment is not null)
            {
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(baselineEnvironment, storageRoot);
            }

            if (baselineEnvironmentId is not null)
            {
                DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(baselineEnvironmentId, storageRoot);
            }
        }
    }

    private static FindingEvidenceArmDisposition ClassifyFocusedEvidenceArm(
        IReadOnlyList<AcceptanceCheckResult> checks)
    {
        if (checks.Any(check => check.FailureClassification is
                AcceptanceFailureClassifications.FocusedSelectionApparatusFailure or
                AcceptanceFailureClassifications.FocusedSelectionReceiptUnreadable))
        {
            return FindingEvidenceArmDisposition.ApparatusFailure;
        }

        if (checks.Count > 0 && checks.All(check => check.Passed))
        {
            return FindingEvidenceArmDisposition.Green;
        }

        return checks
            .Where(check => !check.Passed)
            .Any(check => check.FailingTestIdentities is { Count: > 0 })
                ? FindingEvidenceArmDisposition.Red
                : FindingEvidenceArmDisposition.Inconclusive;
    }

    internal static FindingEvidenceOutcomeReason ClassifyFocusedEvidenceExperiment(
        FocusedEvidenceArmRunResult candidate,
        FocusedEvidenceArmRunResult baseline)
    {
        if (candidate.Disposition == FindingEvidenceArmDisposition.ApparatusFailure ||
            baseline.Disposition == FindingEvidenceArmDisposition.ApparatusFailure)
        {
            return FindingEvidenceOutcomeReason.ApparatusFailure;
        }

        return candidate.Disposition switch
        {
            FindingEvidenceArmDisposition.Red => FindingEvidenceOutcomeReason.CandidateRed,
            FindingEvidenceArmDisposition.Inconclusive => FindingEvidenceOutcomeReason.CandidateInconclusive,
            _ => baseline.Disposition switch
            {
                FindingEvidenceArmDisposition.Green => FindingEvidenceOutcomeReason.VacuousEvidence,
                FindingEvidenceArmDisposition.Red => FindingEvidenceOutcomeReason.ValidEvidence,
                _ => FindingEvidenceOutcomeReason.BaselineInconclusive
            }
        };
    }

    internal static void EmitFocusedEvidenceArmStarted(FindingEvidenceArm arm, string sha) =>
        EmitFocusedEvidenceDiagnostic(
            $"FOCUSED_EVIDENCE_ARM arm={arm.ToString().ToLowerInvariant()} state=started sha={GoalAcceptanceVerifier.QuoteProgressToken(sha)}");

    internal static void EmitFocusedEvidenceArmResolved(FocusedEvidenceArmRunResult arm)
    {
        var failingTestCount = arm.Checks.Sum(check => check.FailingTestIdentities?.Count ?? 0);
        var failed = arm.Checks.FirstOrDefault(check => !check.Passed);
        var failureDetail = failed?.OutputTail is { Length: > 0 } outputTail
            ? $" detail={GoalAcceptanceVerifier.QuoteProgressToken(GoalAcceptanceVerifier.TrimForReceipt(outputTail))}"
            : string.Empty;
        EmitFocusedEvidenceDiagnostic(
            $"FOCUSED_EVIDENCE_ARM arm={arm.Arm.ToString().ToLowerInvariant()} state=resolved " +
            $"sha={GoalAcceptanceVerifier.QuoteProgressToken(arm.Sha)} disposition={ArmDispositionWireValue(arm.Disposition)} " +
            $"accepted={arm.Accepted.ToString().ToLowerInvariant()} passed={arm.Passed.ToString().ToLowerInvariant()} " +
            $"checks={arm.Checks.Count} failing_tests={failingTestCount} summary={GoalAcceptanceVerifier.QuoteProgressToken(arm.Summary)}" +
            failureDetail);
    }

    private static void EmitFocusedEvidenceArmFailed(
        FindingEvidenceArm arm,
        string sha,
        Exception exception) =>
        EmitFocusedEvidenceDiagnostic(
            $"FOCUSED_EVIDENCE_ARM arm={arm.ToString().ToLowerInvariant()} state=failed " +
            $"sha={GoalAcceptanceVerifier.QuoteProgressToken(sha)} exception={exception.GetType().Name} " +
            $"detail={GoalAcceptanceVerifier.QuoteProgressToken(GoalAcceptanceVerifier.TrimForReceipt(exception.Message))}");

    private static void EmitFocusedEvidenceClassification(
        FocusedEvidenceArmRunResult candidate,
        FocusedEvidenceArmRunResult baseline,
        FindingEvidenceOutcomeReason outcomeReason) =>
        EmitFocusedEvidenceDiagnostic(
            $"FOCUSED_EVIDENCE_EXPERIMENT state=classified " +
            $"outcome={FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(outcomeReason)} " +
            $"candidate={ArmDispositionWireValue(candidate.Disposition)} " +
            $"baseline={ArmDispositionWireValue(baseline.Disposition)}");

    private static void EmitFocusedEvidenceDiagnostic(string line)
    {
        Console.WriteLine(line);
        Console.Out.Flush();
    }

    private static FocusedEvidenceArmRunResult InconclusiveBaseline(string summary, string sha = "unavailable") =>
        new(
            FindingEvidenceArm.Baseline,
            sha,
            FindingEvidenceArmDisposition.Inconclusive,
            Accepted: false,
            Passed: false,
            summary,
            Checks: []);

    private static string ArmDispositionWireValue(FindingEvidenceArmDisposition disposition) =>
        disposition switch
        {
            FindingEvidenceArmDisposition.Green => "green",
            FindingEvidenceArmDisposition.Red => "red",
            FindingEvidenceArmDisposition.ApparatusFailure => "apparatus-failure",
            _ => "inconclusive"
        };

    internal static void TryDeleteFocusedEvidenceBaselineDirectory(string baselineRoot, string baselinePath)
    {
        try
        {
            var resolvedRoot = Path.GetFullPath(baselineRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedPath = Path.GetFullPath(baselinePath);
            if (resolvedPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedPath))
            {
                Directory.Delete(resolvedPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // The git worktree removal is authoritative; cleanup is best-effort for partial creation.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the evidence result when a stale handle delays temp-directory cleanup.
        }
    }
}
