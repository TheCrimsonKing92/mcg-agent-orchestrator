using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public enum RecoveryRecommendation
{
    None,
    AutoRetry,
    Deferred,
    CommitAndVerify,
    OperatorNeeded
}

public enum DispatchOutcomeKind
{
    VerifiedSuccess,
    RecoverableSubscriptionLimit,
    PreflightFailure,
    LaunchFailure,
    EmptyOutputFlake,
    SandboxCommitBlocked,
    ProviderNeutralProgressStall,
    ProviderAuthentication,
    ProviderConnectivity,
    ProviderModelRejection,
    DirtyWorktreeRecoverable,
    VerificationInconclusive,
    UnknownFailure,
    ProviderInterruption,
    ProviderBudgetExhausted,
    VerificationIncomplete
}

public sealed record DispatchOutcome(
    DispatchOutcomeKind Kind,
    int ExitCode,
    bool HasZeroByteOutput,
    TimeSpan? RetryAfter,
    TimeSpan? Cooldown,
    RecoveryRecommendation RecoveryRecommendation,
    string EvidenceSummary,
    string ClassifierReceipt = "",
    TaskOutcomeClass OutcomeClass = TaskOutcomeClass.UnknownEra);

public sealed record ProviderSubscriptionCooldown(
    string ProviderName,
    TaskId SourceTaskId,
    DateTimeOffset RetryAfter)
{
    public string BindingKey { get; init; } = "";
    public string EvidenceReceipt { get; init; } = "";
    public DateTimeOffset DispatchStartedAt { get; init; }
}

public sealed record ProviderBudgetExhaustionHold(
    string ProviderName,
    string BindingKey,
    string BindingScope,
    GoalId SourceGoalId,
    TaskId SourceTaskId,
    DateTimeOffset PlacedAt,
    string EvidenceReceipt);

public enum DispatchRoleOutputCapability
{
    RequiresChangeEvidence,
    ReadOnly,
    VerificationOnly
}

public enum DispatchRoleEvidenceRequirement
{
    ScopedRepositoryChange,
    WorkerBuildResult,
    ManualReproduction,
    SourceTrace,
    FocusedEvidenceRequest,
    VerificationMatrix
}

public static class DispatchRoleOutputCapabilities
{
    public static bool TryGet(AgentRole role, out DispatchRoleOutputCapability capability)
    {
        capability = role switch
        {
            AgentRole.Planner or AgentRole.Researcher or AgentRole.Reviewer or AgentRole.Ideation => DispatchRoleOutputCapability.ReadOnly,
            AgentRole.Tester => DispatchRoleOutputCapability.VerificationOnly,
            AgentRole.Developer => DispatchRoleOutputCapability.RequiresChangeEvidence,
            _ => DispatchRoleOutputCapability.RequiresChangeEvidence
        };

        return Enum.IsDefined(role);
    }

    public static bool CanProduceEvidence(
        AgentRole role,
        DispatchRoleEvidenceRequirement requirement)
    {
        if (!TryGet(role, out _))
            return false;

        return requirement switch
        {
            DispatchRoleEvidenceRequirement.ScopedRepositoryChange => role == AgentRole.Developer,
            DispatchRoleEvidenceRequirement.WorkerBuildResult => role is AgentRole.Developer or AgentRole.Tester,
            DispatchRoleEvidenceRequirement.ManualReproduction => role is AgentRole.Developer or AgentRole.Tester,
            DispatchRoleEvidenceRequirement.SourceTrace =>
                role is AgentRole.Researcher or AgentRole.Developer or AgentRole.Tester,
            DispatchRoleEvidenceRequirement.FocusedEvidenceRequest =>
                role is AgentRole.Developer or AgentRole.Tester or AgentRole.Reviewer,
            DispatchRoleEvidenceRequirement.VerificationMatrix => role == AgentRole.Tester,
            _ => false
        };
    }
}

public static class DispatchFailureDiagnosticMarker
{
    public const string Prefix = "@@MCG_ORCHESTRATOR_DIAGNOSTIC_CODE@@";
    public const string ResearcherOutputContractRejected = "researcher-output-contract-rejected";
    public const string ResearcherArtifactPersistenceFailed = "researcher-artifact-persistence-failed";
    public const string PlannerOutputContractRejected = "planner-output-contract-rejected";
    public const string PlannerPlanPersistenceFailed = "planner-plan-persistence-failed";
    public const string WorkerBuildCheckFailed = "worker-build-check-failed";
    public const string WorkerBuildEvidenceMissing = "worker-build-evidence-missing";
    public const string WorkerBuildReceiptStale = "worker-build-receipt-stale";
    public const string WorkerBuildReceiptTreeChanged = "the build receipt does not match the final tree; files changed after build: ";
    public const string WorkerBuildReceiptUncoveredProject = "receipt does not cover project ";
    public const string WrapperProcessExitFailure = "wrapper-process-exit-failure";
    public const string RequiredFileChangeEvidenceMissing = "required-file-change-evidence-missing";
    public const string InScopeFileIgnored = "in-scope-file-ignored";
    public const string WorktreeInspectionFailed = "worktree-inspection-failed";
    public const string ConductorGenerationInterrupted = "conductor-generation-interrupted";

    public static string Format(string code) => $"{Prefix} {code}";

    public static bool SharesMissingEvidenceRecovery(string? rule) =>
        rule is WorkerBuildEvidenceMissing or WorkerBuildReceiptStale;

    public static string FormatWorkerBuildReceiptStale(
        bool treeChanged, IReadOnlyList<string> dirtyPaths, IReadOnlyList<string> uncoveredProjects)
    {
        var diagnostics = new List<string>();
        if (treeChanged)
        {
            var paths = dirtyPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            diagnostics.Add(WorkerBuildReceiptTreeChanged +
                (paths.Length == 0 ? "(no dirty paths recorded)" : string.Join(", ", paths)));
        }
        diagnostics.AddRange(uncoveredProjects.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(project => WorkerBuildReceiptUncoveredProject + project));
        return string.Join("; ", diagnostics) + Environment.NewLine + Format(WorkerBuildReceiptStale);
    }
}

public static class DispatchRejectionDiagnosticMarker
{
    public const string Prefix = "@@MCG_DISPATCH_REJECTION@@";
    public const string VerificationPatternUnmatched = "verification-pattern-unmatched";
    public const string NoChangeEvidence = "no-change-evidence";

    public static string Format(
        bool verificationRecognized,
        int postDispatchCommits,
        string changedPaths) =>
        $"{Prefix} verification_recognized={verificationRecognized.ToString().ToLowerInvariant()} " +
        $"reason={(verificationRecognized ? NoChangeEvidence : VerificationPatternUnmatched)} " +
        $"post_dispatch_commits={postDispatchCommits} " +
        $"changed_paths_base64={Convert.ToBase64String(Encoding.UTF8.GetBytes(changedPaths))}";

    public static bool TryParse(
        string standardError,
        out bool verificationRecognized,
        out string reason,
        out int postDispatchCommits,
        out string changedPaths)
    {
        foreach (var line in standardError.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith(Prefix + " ", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in line[(Prefix.Length + 1)..]
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = field.IndexOf('=');
                if (separator > 0)
                {
                    fields[field[..separator]] = field[(separator + 1)..];
                }
            }
            if (!fields.TryGetValue("verification_recognized", out var recognizedText) ||
                !bool.TryParse(recognizedText, out verificationRecognized) ||
                !fields.TryGetValue("reason", out reason) ||
                reason != (verificationRecognized ? NoChangeEvidence : VerificationPatternUnmatched) ||
                !fields.TryGetValue("post_dispatch_commits", out var commitsText) ||
                !int.TryParse(commitsText, out postDispatchCommits) ||
                !fields.TryGetValue("changed_paths_base64", out var changedPathsBase64))
            {
                continue;
            }

            try
            {
                changedPaths = Encoding.UTF8.GetString(Convert.FromBase64String(changedPathsBase64));
                return true;
            }
            catch (FormatException)
            {
                // A malformed marker is not positive evidence. Continue in case an earlier valid marker exists.
            }
        }

        verificationRecognized = false;
        reason = string.Empty;
        postDispatchCommits = 0;
        changedPaths = string.Empty;
        return false;
    }
}

public static partial class DispatchFailureClassifier
{
    private sealed record OrchestratorAuthoredFailure(TaskOutcomeRule Rule, string Description,
        RecoveryRecommendation Recommendation = RecoveryRecommendation.OperatorNeeded);

    private static readonly IReadOnlyDictionary<string, OrchestratorAuthoredFailure> OrchestratorAuthoredFailures =
        new Dictionary<string, OrchestratorAuthoredFailure>(StringComparer.Ordinal)
        {
            [DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected] = new(
                TaskOutcomeRules.ResearcherOutputContractRejected,
                "Researcher output contract rejected the captured research."),
            [DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed] = new(
                TaskOutcomeRules.ResearcherArtifactPersistenceFailed,
                "Researcher output contract could not persist the accepted research artifact."),
            [DispatchFailureDiagnosticMarker.PlannerOutputContractRejected] = new(
                TaskOutcomeRules.PlannerOutputContractRejected,
                "Planner output contract rejected the captured plan."),
            [DispatchFailureDiagnosticMarker.PlannerPlanPersistenceFailed] = new(
                TaskOutcomeRules.PlannerPlanPersistenceFailed,
                "Planner output contract could not persist the accepted plan."),
            [DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed] = new(
                TaskOutcomeRules.WorkerBuildCheckFailed,
                "Deterministic worker build check failed."),
            [DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing] = new(
                TaskOutcomeRules.WorkerBuildEvidenceMissing,
                "Required worker build evidence was not reported."),
            [DispatchFailureDiagnosticMarker.WorkerBuildReceiptStale] = new(
                TaskOutcomeRules.WorkerBuildReceiptStale,
                "Worker build receipt does not match the final tree."),
            [DispatchFailureDiagnosticMarker.WrapperProcessExitFailure] = new(
                TaskOutcomeRules.WrapperProcessExitFailure,
                "Wrapper process exited nonzero after the selected child succeeded, without usable completion evidence."),
            [DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing] = new(
                TaskOutcomeRules.RequiredFileChangeEvidenceMissing,
                "Developer/Tester dispatch did not produce required relevant file-change evidence."),
            [DispatchFailureDiagnosticMarker.InScopeFileIgnored] = new(
                TaskOutcomeRules.InScopeFileIgnored,
                "Candidate snapshot left out in-scope files that ignore rules exclude.",
                RecoveryRecommendation.AutoRetry),
            [DispatchFailureDiagnosticMarker.WorktreeInspectionFailed] = new(
                TaskOutcomeRules.WorktreeInspectionFailed,
                "Completed dispatch worktree inspection failed.")
        };

    public const int RecoverableSubscriptionLimitReviewThreshold = 2;
    public static readonly TimeSpan SilentLaunchFailureMaxDuration = TimeSpan.FromMinutes(1);

    public static DispatchOutcome ClassifyProviderFailure(
        ProviderFailureKind failureKind,
        int exitCode,
        bool hasZeroByteOutput,
        string evidenceSummary)
    {
        return failureKind switch
        {
            ProviderFailureKind.RateLimit => DispatchClassifierReceipt.WithClassifierReceipt(
                TaskOutcomeRules.ProviderRateLimit,
                new DispatchOutcome(
                DispatchOutcomeKind.RecoverableSubscriptionLimit,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                evidenceSummary),
                exitCode),
            ProviderFailureKind.BudgetExhausted => DispatchClassifierReceipt.WithClassifierReceipt(
                TaskOutcomeRules.ProviderBudgetExhausted,
                new DispatchOutcome(
                DispatchOutcomeKind.ProviderBudgetExhausted,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.OperatorNeeded,
                evidenceSummary),
                exitCode),
            ProviderFailureKind.Connectivity => DispatchClassifierReceipt.WithClassifierReceipt(
                TaskOutcomeRules.ProviderConnectivity,
                new DispatchOutcome(
                DispatchOutcomeKind.ProviderConnectivity,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                evidenceSummary),
                exitCode),
            ProviderFailureKind.Sandbox1312 => DispatchClassifierReceipt.WithClassifierReceipt(
                TaskOutcomeRules.ProviderSandboxLaunch1312,
                new DispatchOutcome(
                DispatchOutcomeKind.LaunchFailure,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                evidenceSummary),
                exitCode),
            _ => DispatchClassifierReceipt.WithClassifierReceipt(
                TaskOutcomeRules.ProviderUnknown,
                new DispatchOutcome(
                DispatchOutcomeKind.UnknownFailure,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.OperatorNeeded,
                evidenceSummary),
                exitCode)
        };
    }

    private static readonly Regex PassedCountPattern = new(
        @"\bPassed:\s*[1-9]\d*\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Common test-runner phrasings the colon form misses: "4 passed", "4 tests passed",
    // "4 of 4 passed", and "tests: pass" / "tests pass". Test runners and agents report results
    // these ways far more often than "Passed: N", so recognising them prevents false "no evidence"
    // failures on genuinely-verified work (the acceptance gate remains the authoritative check).
    private static readonly Regex PassedWordPattern = new(
        @"\b[1-9]\d*\s+(?:[\w.-]+\s+){0,3}passed\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TestsPassPattern = new(
        @"\btests?\s*[:=]?\s*pass(?:ed|es|ing)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FractionPattern = new(
        @"\b\d+\s*/\s*\d+\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExitCodeZeroPattern = new(
        @"\bexit code\s*0\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CommitShaPattern = new(
        @"\b[0-9a-f]{7,64}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsRecoverableSubscriptionLimitFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        if (IsDirtyDispatchGuardFailure(verification))
        {
            return false;
        }

        if (HasWorkerEvidenceThatOutranksSubscriptionLimit(
            verification,
            verification.WorkerResultPresent,
            verification.HasCommittedChanges))
        {
            return false;
        }

        if (ProviderFailureSignals.TryGetProviderAuthenticationLine(verification, out _) ||
            ProviderFailureSignals.TryGetProviderConnectivityLine(verification, out _))
        {
            return false;
        }

        return TryGetRecoverableSubscriptionLimitLine(verification, out _);
    }

    public static bool HasRecoverableSubscriptionLimitEvidence(TaskVerificationRecord verification) =>
        !IsDirtyDispatchGuardFailure(verification) &&
        TryGetRecoverableSubscriptionLimitLine(verification, out _);

    public static bool HasVerificationEvidenceInOutput(string standardOutput, string standardError) =>
        HasVerificationEvidence(standardOutput, standardError);

    public static bool HasWorkerResultDeferralInOutput(string standardOutput) =>
        HasWorkerResultDeferral(DispatchEvidenceSummaries.SplitEvidenceLines(standardOutput));

    public static bool IsTransientEmptyOutputDispatchFlake(TaskVerificationRecord verification)
    {
        if (DispatchEvidenceSummaries.IsPreflightFailure(verification))
        {
            return false;
        }

        if (IsDispatchRecoveryPolicyDiagnostic(verification))
        {
            return false;
        }

        if (HasArtifactEvidence(verification.WorkerResultPresent, verification.HasCommittedChanges))
        {
            return false;
        }

        if (IsSilentLaunchFailure(verification))
        {
            return true;
        }

        // exit 0 with evidence the worker actually produced output is never a transient empty-output flake.
        // The heartbeat stdout-byte count is the flush-race-proof signal: a worker that streamed bytes per its
        // heartbeat genuinely ran (the out.log file read can race the exit flush and momentarily report empty,
        // which mis-flaked successful exit-0 workers ~21x — backlog 58407042). A genuine exit-0 STALL reports
        // zero heartbeat bytes and no artifact, so it still falls through to the flake path for failover.
        if (verification.ExitCode == 0 &&
            verification.HeartbeatStandardOutputBytes > 0)
        {
            return false;
        }

        if (HasStandardOutputFileBytes(verification))
        {
            return false;
        }

        if (verification.ExitCode != 0)
        {
            return string.IsNullOrWhiteSpace(verification.StandardOutput) &&
                !HasSubstantiveStandardError(verification.StandardError);
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardOutput) ||
            !string.IsNullOrWhiteSpace(verification.StandardError))
        {
            return false;
        }

        return true;
    }

    public static bool IsSilentLaunchFailure(TaskVerificationRecord verification)
    {
        if (verification.ExitCode == 0 ||
            DispatchEvidenceSummaries.IsPreflightFailure(verification) ||
            IsDispatchRecoveryPolicyDiagnostic(verification) ||
            IsDirtyDispatchGuardFailure(verification) ||
            HasArtifactEvidence(verification.WorkerResultPresent, verification.HasCommittedChanges) ||
            !HasZeroByteStandardOutput(verification) ||
            !HasZeroByteStandardError(verification))
        {
            return false;
        }

        if (verification.DispatchStartedAt is not { } startedAt)
            return false;

        var duration = verification.CompletedAt - startedAt;
        return duration >= TimeSpan.Zero && duration <= SilentLaunchFailureMaxDuration;
    }

    private static bool IsDispatchRecoveryPolicyDiagnostic(TaskVerificationRecord verification) =>
        verification.StandardError.Contains("Dispatch recovery policy action='mark-stale'", StringComparison.Ordinal) ||
        verification.StandardError.Contains("Dispatch recovery policy action='retry-stale'", StringComparison.Ordinal) ||
        verification.StandardError.Contains("Dispatch recovery policy action='budget-exhausted'", StringComparison.Ordinal);

    private static bool HasZeroByteStandardOutput(TaskVerificationRecord verification)
    {
        if (!string.IsNullOrWhiteSpace(verification.StandardOutputPath) &&
            File.Exists(verification.StandardOutputPath))
        {
            return new FileInfo(verification.StandardOutputPath).Length == 0;
        }

        return verification.StandardOutput.Length == 0;
    }

    internal static bool HasStandardOutputFileBytes(TaskVerificationRecord verification)
    {
        if (string.IsNullOrWhiteSpace(verification.StandardOutputPath) ||
            !File.Exists(verification.StandardOutputPath))
        {
            return false;
        }

        return new FileInfo(verification.StandardOutputPath).Length > 0;
    }

    private static bool HasZeroByteStandardError(TaskVerificationRecord verification)
    {
        // The redirected file is authoritative for worker bytes, but the verification text can
        // contain orchestrator-injected diagnostics that never appeared in that file. Positive
        // failure evidence must win over an empty file; bookkeeping receipts do not constitute a
        // worker verdict and are deliberately ignored here.
        if (HasSubstantiveStandardError(verification.StandardError))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardErrorPath) &&
            File.Exists(verification.StandardErrorPath))
        {
            return new FileInfo(verification.StandardErrorPath).Length == 0;
        }

        return true;
    }

    private static bool HasSubstantiveStandardError(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return false;
        }

        return standardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => !IsRunnerBookkeepingLine(line));
    }

    internal static bool IsRunnerBookkeepingLine(string line) =>
        line.StartsWith("RESOURCE ", StringComparison.Ordinal) ||
        line.StartsWith("CLASSIFIER ", StringComparison.Ordinal) ||
        line.StartsWith(DispatchFailureDiagnosticMarker.Prefix, StringComparison.Ordinal) ||
        line.StartsWith(DispatchRejectionDiagnosticMarker.Prefix, StringComparison.Ordinal) ||
        line.StartsWith("Dispatch recovery policy action=", StringComparison.Ordinal) ||
        line.StartsWith("Developer/Tester dispatch did not produce required relevant file-change evidence.", StringComparison.Ordinal);

    public static bool TryBuildDirtyDispatchRecovery(TaskSpec task, out DirtyDispatchRecovery recovery)
    {
        recovery = DirtyDispatchRecovery.None;
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester) ||
            task.LastVerification is not { Succeeded: false } verification ||
            !IsDirtyDispatchGuardFailure(verification))
        {
            return false;
        }

        var changedFiles = ExtractChangedFiles(verification.StandardError);
        var verificationEvidence = ExtractVerificationEvidence(verification.StandardOutput, verification.StandardError);
        var label = verificationEvidence.Length > 0 ? "dirty-useful" : "dirty-unverified";
        recovery = new DirtyDispatchRecovery(
            label,
            changedFiles,
            verificationEvidence,
            verification.WorkingDirectory);
        return true;
    }

    public static DispatchOutcome Classify(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent = false,
        bool hasCommittedChanges = false)
    {
        return Classify(task, verification, verification.ProviderFailureKind, workerResultPresent, hasCommittedChanges);
    }

    public static DispatchOutcome Classify(
        TaskSpec task,
        TaskVerificationRecord verification,
        ProviderFailureKind providerFailureKind,
        bool workerResultPresent = false,
        bool hasCommittedChanges = false)
        => ClassifyCore(task, verification, providerFailureKind, workerResultPresent, hasCommittedChanges,
            WorkerResultBlockers.AssessSubscriptionVerification);

    public static DispatchOutcome ClassifyStoredRound(TaskSpec task, TaskVerificationRecord verification)
        => ClassifyCore(task, verification, verification.ProviderFailureKind, false, false,
            WorkerResultBlockers.AssessStoredSubscriptionVerification);

    private static DispatchOutcome ClassifyCore(
        TaskSpec task,
        TaskVerificationRecord verification,
        ProviderFailureKind providerFailureKind,
        bool workerResultPresent,
        bool hasCommittedChanges,
        Func<TaskSpec, TaskVerificationRecord, VerificationTerminalContractAssessment> assessTerminalContract)
    {
        if (ConductorGenerationInterruptionClassification.IsMarked(verification))
            return BuildOutcome(TaskOutcomeRules.ConductorGenerationInterrupted, task, verification,
                workerResultPresent, hasCommittedChanges, ConductorGenerationInterruptionClassification.Outcome(verification));

        if (ProviderFailureSignals.IsProviderInterruptionFailure(verification))
        {
            return BuildOutcome(
                TaskOutcomeRules.ProviderInterruption,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.ProviderInterruption,
                verification.ExitCode,
                HasZeroByteStandardOutput(verification),
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                CodexRawAuditStream.TryGetFailedTurnMessage(verification.StandardOutputPath, out var message)
                    ? $"provider interruption: {message}"
                    : DispatchEvidenceSummaries.BuildEvidenceSummary(verification)));
        }

        providerFailureKind = providerFailureKind == ProviderFailureKind.Unknown
            ? verification.ProviderFailureKind
            : providerFailureKind;
        workerResultPresent = workerResultPresent || verification.WorkerResultPresent;
        hasCommittedChanges = hasCommittedChanges || verification.HasCommittedChanges || HasDispatchResultCommitEvidence(task);
        var exitCode = verification.ExitCode;
        var hasZeroByteOutput = HasZeroByteStandardOutput(verification);

        var terminalContract = assessTerminalContract(task, verification);
        if (!terminalContract.AllowsCompletion)
        {
            // Preserve positively identified provider faults before diagnosing absent reporting.
            if (providerFailureKind != ProviderFailureKind.Unknown ||
                IsRecoverableSubscriptionLimitFailure(verification) ||
                ProviderFailureSignals.IsRecoverableProviderAuthenticationFailure(verification) ||
                ProviderFailureSignals.IsRecoverableProviderConnectivityFailure(verification))
            {
                if (ProviderFailureSignals.IsRecoverableProviderAuthenticationFailure(verification))
                    return BuildOutcome(TaskOutcomeRules.ProviderAuthentication, task, verification, false, hasCommittedChanges,
                        new DispatchOutcome(DispatchOutcomeKind.ProviderAuthentication, exitCode, hasZeroByteOutput,
                            null, null, RecoveryRecommendation.OperatorNeeded, BuildProviderAuthenticationEvidenceSummary(task, verification)));
                var fault = providerFailureKind != ProviderFailureKind.Unknown ? providerFailureKind :
                    IsRecoverableSubscriptionLimitFailure(verification) ? ProviderFailureKind.RateLimit :
                    ProviderFailureKind.Connectivity;
                var providerOutcome = ClassifyProviderFailure(fault, exitCode, hasZeroByteOutput, DispatchEvidenceSummaries.BuildEvidenceSummary(verification));
                return IsRecoverableSubscriptionLimitFailure(verification)
                    ? BuildOutcome(TaskOutcomeRules.SubscriptionLimit, task, verification, false, hasCommittedChanges, providerOutcome)
                    : providerOutcome;
            }
            // Explicit current-attempt detachment retains its existing interruption retry owner.
            if (terminalContract.Status == VerificationTerminalContractStatus.Missing &&
                IsDetachedWithoutWorkerResult(task, verification, workerResultPresent))
                return BuildDetachedWithoutWorkerResultOutcome(task, verification, workerResultPresent, hasCommittedChanges);
            return BuildOutcome(TaskOutcomeRules.VerificationTerminalContractIncomplete,
                task, verification, false, hasCommittedChanges,
                new DispatchOutcome(DispatchOutcomeKind.VerificationIncomplete, exitCode, hasZeroByteOutput,
                    null, null, RecoveryRecommendation.AutoRetry,
                    $"terminal_contract={terminalContract.Status}; {terminalContract.Diagnostic}"));
        }

        if (terminalContract.Status == VerificationTerminalContractStatus.Valid &&
            task.RequiredRole == AgentRole.Reviewer &&
            (!WorkerResultBlockers.TryFindPassVerdict(verification) || WorkerResultBlockers.TryFindBlocker(verification, out _)))
            return BuildOutcome(TaskOutcomeRules.UnknownFailure, task, verification, true, hasCommittedChanges,
                new DispatchOutcome(DispatchOutcomeKind.UnknownFailure, exitCode, hasZeroByteOutput,
                    null, null, RecoveryRecommendation.OperatorNeeded, "Reviewer terminal contract does not report an unblocked pass."));

        if (task.RequiredRole == AgentRole.Tester &&
            workerResultPresent &&
            !IsDirtyDispatchGuardFailure(verification) &&
            WorkerResultBlockers.TryGetTestsStatus(verification, out var inconclusiveStatus) &&
            (inconclusiveStatus == WorkerResultBlockers.TestsStatus.Inconclusive ||
             (terminalContract.Status == VerificationTerminalContractStatus.Valid && inconclusiveStatus == WorkerResultBlockers.TestsStatus.NotRun &&
              WorkerResultBlockers.TryGetBlockersStatus(verification, out var notRunBlockers) && notRunBlockers == WorkerResultBlockers.BlockersStatus.None)))
        {
            WorkerResultBlockers.TryFindTests(verification, out var testsEvidence);
            var evidenceSummary = $"structured Tester verification is inconclusive: {testsEvidence}";
            if (WorkerResultBlockers.TryFindBlocker(verification, out var conflictingBlocker))
            {
                evidenceSummary += $"; schema conflict retained for audit: blockers: {conflictingBlocker}";
            }

            return BuildOutcome(
                TaskOutcomeRules.TesterVerificationInconclusive,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                    DispatchOutcomeKind.VerificationInconclusive,
                    exitCode,
                    hasZeroByteOutput,
                    null,
                    null,
                    RecoveryRecommendation.AutoRetry,
                    evidenceSummary));
        }

        if (WorkerResultBlockers.TryFindTesterWorkerResultBlocker(task, verification, out var testerBlocker) &&
            !(verification.Succeeded && workerResultPresent &&
              WorkerResultBlockers.TryFindPremiseInvalidEvidence(verification, out _)))
        {
            return BuildOutcome(
                TaskOutcomeRules.TesterWorkerResultBlocker,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                    DispatchOutcomeKind.UnknownFailure,
                    exitCode,
                    hasZeroByteOutput,
                    null,
                    null,
                    RecoveryRecommendation.OperatorNeeded,
                    $"Tester WORKER_RESULT reported blockers: {testerBlocker}"));
        }

        if (verification.Succeeded &&
            (!DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var roleCapability) ||
             roleCapability != DispatchRoleOutputCapability.ReadOnly) &&
            WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) &&
            testsStatus == WorkerResultBlockers.TestsStatus.Fail)
        {
            return BuildOutcome(
                TaskOutcomeRules.SucceededWorkerResultFailingTests,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.UnknownFailure,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.OperatorNeeded,
                DispatchEvidenceSummaries.BuildProviderModelRejectionEvidenceSummary(verification)));
        }

        if (verification.Succeeded &&
            (!DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var unstructuredRoleCapability) ||
             unstructuredRoleCapability != DispatchRoleOutputCapability.ReadOnly) &&
            WorkerResultBlockers.TryFindUnstructuredFailingTests(verification, out var unstructuredTests))
        {
            return BuildOutcome(
                TaskOutcomeRules.SucceededWorkerResultUnstructuredFailingTests,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                    DispatchOutcomeKind.UnknownFailure,
                    exitCode,
                    hasZeroByteOutput,
                    null,
                    null,
                    RecoveryRecommendation.OperatorNeeded,
                    $"Unstructured tests field: no leading status word and the text reports failure; restate it as 'fail - <evidence>' or 'pass - <evidence>': {DispatchEvidenceSummaries.TruncateEvidence(unstructuredTests)}"));
        }

        if (task.RequiredRole == AgentRole.Developer &&
            WorkerResultBlockers.TryFindMalformedEvidenceBoundOutcome(
                verification.AuthoritativeStandardOutput ?? verification.StandardOutput,
                out var outputContractDiagnostic))
        {
            return BuildOutcome(
                TaskOutcomeRules.UnknownFailure,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                    DispatchOutcomeKind.UnknownFailure,
                    exitCode,
                    hasZeroByteOutput,
                    null,
                    null,
                    RecoveryRecommendation.OperatorNeeded,
                    $"Malformed WORKER_RESULT structured outcome: {outputContractDiagnostic}"));
        }

        if (task.RequiredRole is AgentRole.Developer or AgentRole.Tester &&
            verification.Succeeded &&
            workerResultPresent &&
            WorkerResultBlockers.TryFindPremiseInvalidEvidence(verification, out var premiseEvidence))
        {
            return BuildOutcome(
                TaskOutcomeRules.PremiseRefuted,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                    DispatchOutcomeKind.UnknownFailure,
                    exitCode,
                    hasZeroByteOutput,
                    null,
                    null,
                    RecoveryRecommendation.OperatorNeeded,
                    $"{task.RequiredRole} refuted the goal premise: {premiseEvidence}"));
        }

        if (task.RequiredRole == AgentRole.Developer &&
            WorkerResultBlockers.GetAssignedScopeComplete(verification) is false)
        {
            return BuildOutcome(
                TaskOutcomeRules.IncompleteScopeDeclaration,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                    DispatchOutcomeKind.UnknownFailure,
                    exitCode,
                    hasZeroByteOutput,
                    null,
                    null,
                    RecoveryRecommendation.AutoRetry,
                    "Developer declared the assigned implementation scope incomplete."));
        }

        if (HasGreenCommittedWorkerResultEvidence(verification, workerResultPresent, hasCommittedChanges))
        {
            return BuildOutcome(
                TaskOutcomeRules.CommittedWorkerResultEvidence,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.VerifiedSuccess,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.None,
                DispatchEvidenceSummaries.BuildEvidenceSummary(verification)));
        }

        if (IsRetryRoundVerifiedNoNewCommit(task, verification, workerResultPresent, hasCommittedChanges))
        {
            return BuildOutcome(
                TaskOutcomeRules.VerifiedNoNewCommit,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.VerifiedSuccess,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.None,
                DispatchEvidenceSummaries.BuildVerifiedNoNewCommitEvidenceSummary(task, verification)));
        }

        if (IsDeveloperVerifiedNoChangeRound(task, verification, workerResultPresent, hasCommittedChanges))
        {
            return BuildOutcome(
                TaskOutcomeRules.VerifiedNoChangeRound,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.VerifiedSuccess,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.None,
                DispatchEvidenceSummaries.BuildVerifiedNoChangeRoundEvidenceSummary(task, verification)));
        }

        if (IsDeveloperDeferredNoChangeRound(task, verification, workerResultPresent, hasCommittedChanges))
        {
            return BuildOutcome(
                TaskOutcomeRules.DeferredNoChangeRound,
                task, verification, workerResultPresent, hasCommittedChanges,
                new DispatchOutcome(
                    DispatchOutcomeKind.VerifiedSuccess, exitCode, hasZeroByteOutput,
                    null, null, RecoveryRecommendation.None,
                    $"deferred-no-change-round: candidate {task.LastDispatch!.BaseCommit}"));
        }

        if (task.RequiredRole == AgentRole.Developer &&
            DeferredNoChangeOutcome.ContainsMarker(verification.StandardError))
        {
            return BuildOutcome(
                TaskOutcomeRules.UnknownFailure,
                task, verification, workerResultPresent, hasCommittedChanges,
                new DispatchOutcome(
                    DispatchOutcomeKind.UnknownFailure, exitCode, hasZeroByteOutput,
                    null, null, RecoveryRecommendation.OperatorNeeded,
                    "Deferred no-change marker disagrees with authoritative worker output."));
        }

        if (IsRetryRoundWithoutCommitOrDeferral(task, verification, workerResultPresent, hasCommittedChanges))
        {
            return BuildOutcome(
                TaskOutcomeRules.RetryRoundProducedNoCommitAndNoDeferral,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.UnknownFailure,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                "retry round produced no commit and no deferral"));
        }

        if (IsDetachedWithoutWorkerResult(task, verification, workerResultPresent))
        {
            return BuildDetachedWithoutWorkerResultOutcome(task, verification, workerResultPresent, hasCommittedChanges);
        }

        if (verification.Succeeded &&
            HasDispatchCompletionEvidence(task, verification, workerResultPresent, hasCommittedChanges))
        {
            return BuildOutcome(
                TaskOutcomeRules.SucceededDispatchCompletionEvidence,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.VerifiedSuccess,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.None,
                DispatchEvidenceSummaries.BuildEvidenceSummary(verification)));
        }

        if (TryBuildDirtyDispatchRecovery(task, out _))
        {
            return BuildOutcome(
                TaskOutcomeRules.DirtyDispatchRecovery,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.DirtyWorktreeRecoverable,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.OperatorNeeded,
                DispatchEvidenceSummaries.BuildEvidenceSummary(verification)));
        }

        if (providerFailureKind == ProviderFailureKind.Sandbox1312)
        {
            var providerOutcome = ClassifyProviderFailure(
                providerFailureKind,
                exitCode,
                hasZeroByteOutput,
                DispatchEvidenceSummaries.BuildEvidenceSummary(verification));
            return BuildOutcome(
                TaskOutcomeRules.ProviderSandboxLaunch1312,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                providerOutcome);
        }

        if (providerFailureKind == ProviderFailureKind.BudgetExhausted &&
            !HasWorkerEvidenceThatOutranksSubscriptionLimit(verification, workerResultPresent, hasCommittedChanges))
        {
            var providerOutcome = ClassifyProviderFailure(
                providerFailureKind,
                exitCode,
                hasZeroByteOutput,
                DispatchEvidenceSummaries.BuildProviderBudgetExhaustionEvidenceSummary(task, verification));
            return BuildOutcome(
                TaskOutcomeRules.ProviderBudgetExhausted,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                providerOutcome);
        }

        if (TryClassifyWorkerSandboxRefusal(task, verification, workerResultPresent, hasCommittedChanges, out var sandboxRefusal))
        {
            return sandboxRefusal;
        }

        if (ProviderFailureSignals.IsRecoverableProviderAuthenticationFailure(verification))
        {
            return BuildOutcome(
                TaskOutcomeRules.ProviderAuthentication,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.ProviderAuthentication,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.OperatorNeeded,
                BuildProviderAuthenticationEvidenceSummary(task, verification)));
        }

        if (ProviderFailureSignals.IsRecoverableProviderConnectivityFailure(verification))
        {
            return BuildOutcome(
                TaskOutcomeRules.ProviderConnectivity,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.ProviderConnectivity,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                DispatchEvidenceSummaries.BuildProviderConnectivityEvidenceSummary(verification)));
        }

        if (IsSubscriptionProviderCliDispatch(task) &&
            IsRecoverableSubscriptionLimitFailure(verification))
        {
            TimeSpan? retryAfter = null;
            if (ProviderLimitSignals.TryGetSubscriptionLimitRetryAfter(verification, out var retryAfterAbs))
            {
                var now = DateTimeOffset.UtcNow;
                if (retryAfterAbs > now)
                    retryAfter = retryAfterAbs - now;
            }
            return BuildOutcome(
                TaskOutcomeRules.SubscriptionLimit,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.RecoverableSubscriptionLimit,
                exitCode,
                hasZeroByteOutput,
                retryAfter,
                null,
                retryAfter.HasValue ? RecoveryRecommendation.Deferred : RecoveryRecommendation.AutoRetry,
                DispatchEvidenceSummaries.BuildRecoverableSubscriptionLimitEvidenceSummary(verification)));
        }

        if (DispatchEvidenceSummaries.IsPreflightFailure(verification))
        {
            return BuildOutcome(
                TaskOutcomeRules.PreflightFailure,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.PreflightFailure,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.OperatorNeeded,
                DispatchEvidenceSummaries.BuildPreflightFailureEvidenceSummary(verification)));
        }

        if (IsSilentLaunchFailure(verification))
        {
            return BuildOutcome(
                TaskOutcomeRules.SilentLaunchFailure,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.LaunchFailure,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                "root process exited nonzero before either redirected stream received worker output"));
        }

        if (IsTransientEmptyOutputDispatchFlake(verification))
        {
            return BuildOutcome(
                TaskOutcomeRules.EmptyOutputFlake,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.EmptyOutputFlake,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                string.Empty));
        }

        if (IsSandboxCommitBlockedFailure(task, verification))
        {
            return BuildOutcome(
                TaskOutcomeRules.SandboxCommitBlocked,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.SandboxCommitBlocked,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.CommitAndVerify,
                DispatchEvidenceSummaries.BuildEvidenceSummary(verification)));
        }

        if (ProviderFailureSignals.IsProviderNeutralProgressStallFailure(verification))
        {
            return BuildOutcome(
                TaskOutcomeRules.ProviderNeutralProgressStall,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.ProviderNeutralProgressStall,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                DispatchEvidenceSummaries.BuildEvidenceSummary(verification)));
        }

        if (exitCode != 0 && DispatchEvidenceSummaries.TryGetDispatchHostMaxRuntimeReapSummary(verification, out var reapSummary))
        {
            return BuildOutcome(
                TaskOutcomeRules.ReapedMaxRuntime,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.UnknownFailure,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.OperatorNeeded,
                reapSummary));
        }

        if (providerFailureKind == ProviderFailureKind.RateLimit &&
            !HasWorkerEvidenceThatOutranksSubscriptionLimit(verification, workerResultPresent, hasCommittedChanges) &&
            TryGetRecoverableSubscriptionLimitLine(verification, out _))
        {
            return BuildOutcome(
                TaskOutcomeRules.ProviderRateLimit,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.RecoverableSubscriptionLimit,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                DispatchEvidenceSummaries.BuildRecoverableSubscriptionLimitEvidenceSummary(verification)));
        }

        if (ProviderFailureSignals.IsRecoverableProviderModelRejectionFailure(verification, task.RequiredRole))
        {
            return BuildOutcome(
                TaskOutcomeRules.ProviderModelRejection,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.ProviderModelRejection,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.OperatorNeeded,
                DispatchEvidenceSummaries.BuildEvidenceSummary(verification)));
        }

        if (TryGetOrchestratorAuthoredFailure(verification, out var authoredFailure))
        {
            var verifiedNoChange = IsVerifiedNoChangeRoundWithRecognisedEvidence(
                authoredFailure,
                verification,
                workerResultPresent,
                hasCommittedChanges);

            return BuildOutcome(
                verifiedNoChange ? TaskOutcomeRules.VerifiedNoChangeRound : authoredFailure.Rule,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                verifiedNoChange ? DispatchOutcomeKind.VerifiedSuccess : DispatchOutcomeKind.UnknownFailure,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                verifiedNoChange ? RecoveryRecommendation.None : authoredFailure.Recommendation,
                verifiedNoChange
                    ? DispatchEvidenceSummaries.BuildVerifiedNoChangeRoundEvidenceSummary(task, verification)
                    : BuildOrchestratorAuthoredFailureEvidenceSummary(authoredFailure, verification)));
        }

        if (verification.ExitCode != 0 && DispatchEvidenceSummaries.HasScriptingFailureEvidence(verification))
        {
            return BuildOutcome(
                TaskOutcomeRules.RealFailure,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                DispatchOutcomeKind.UnknownFailure,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.AutoRetry,
                DispatchEvidenceSummaries.BuildRealFailureEvidenceSummary(verification)));
        }

        return BuildOutcome(
            TaskOutcomeRules.UnknownFailure,
            task,
            verification,
            workerResultPresent,
            hasCommittedChanges,
            new DispatchOutcome(
            DispatchOutcomeKind.UnknownFailure,
            exitCode,
            hasZeroByteOutput,
            null,
            null,
            RecoveryRecommendation.OperatorNeeded,
            DispatchEvidenceSummaries.BuildUnknownFailureEvidenceSummary(verification)));
    }

    private static bool TryGetOrchestratorAuthoredFailure(
        TaskVerificationRecord verification,
        out OrchestratorAuthoredFailure failure)
    {
        var markerPrefix = DispatchFailureDiagnosticMarker.Prefix + " ";
        // Markers are appended in causal order by the completion path. When more than one site
        // fires, the first marker is the proximate authored failure and therefore owns the label.
        foreach (var line in verification.StandardError.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(markerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var code = line[markerPrefix.Length..];
            if (OrchestratorAuthoredFailures.TryGetValue(code, out failure!))
            {
                return true;
            }
        }

        failure = null!;
        return false;
    }

    private static string BuildOrchestratorAuthoredFailureEvidenceSummary(
        OrchestratorAuthoredFailure failure,
        TaskVerificationRecord verification)
    {
        var description = failure.Description;
        if (failure.Rule == TaskOutcomeRules.WorkerBuildReceiptStale)
        {
            var diagnostic = DispatchEvidenceSummaries.GetSubstantiveStandardErrorLines(verification).LastOrDefault(line =>
                line.StartsWith(DispatchFailureDiagnosticMarker.WorkerBuildReceiptTreeChanged, StringComparison.Ordinal) ||
                line.StartsWith(DispatchFailureDiagnosticMarker.WorkerBuildReceiptUncoveredProject, StringComparison.Ordinal));
            return diagnostic is null ? description : $"{description} {DispatchEvidenceSummaries.TruncateEvidence(diagnostic)}";
        }
        var substantiveLines = DispatchEvidenceSummaries.GetSubstantiveStandardErrorLines(verification)
            .TakeLast(3)
            .ToArray();
        return substantiveLines.Length == 0
            ? description
            : $"{description} diagnostic stderr-tail: {DispatchEvidenceSummaries.TruncateEvidence(string.Join(" | ", substantiveLines))}";
    }

    private static DispatchOutcome BuildOutcome(
        TaskOutcomeRule rule,
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges,
        DispatchOutcome outcome)
    {
        var outcomeClass = verification.ReconciledToSuccess
            ? TaskOutcomeClass.ReconciledToSuccess
            : rule.Class;
        return (outcome with { OutcomeClass = outcomeClass }) with
        {
            ClassifierReceipt = DispatchClassifierReceipt.AppendEvidenceReceipt(
                DispatchClassifierReceipt.BuildClassifierReceipt(rule, outcomeClass, task, verification, workerResultPresent, hasCommittedChanges, outcome.Kind),
                outcome)
        };
    }

    internal static bool HasDispatchResultCommitEvidence(TaskSpec task)
    {
        if (task.LastDispatch is not { } dispatch ||
            string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            string.IsNullOrWhiteSpace(dispatch.ResultCommit))
        {
            return false;
        }

        return !string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasVerificationEvidence(string standardOutput, string standardError)
    {
        var output = $"{standardOutput}\n{standardError}";
        if (output.Contains("test run successful", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hasVerificationTerm =
            output.Contains("test", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("suite", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("verification", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("smoke", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("check", StringComparison.OrdinalIgnoreCase);

        return hasVerificationTerm &&
            (PassedCountPattern.IsMatch(output) ||
             PassedWordPattern.IsMatch(output) ||
             TestsPassPattern.IsMatch(output) ||
             FractionPattern.IsMatch(output) ||
             ExitCodeZeroPattern.IsMatch(output));
    }

    internal static bool HasVerificationEvidence(TaskVerificationRecord verification) =>
        DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true)
            .Any(line => HasVerificationEvidence(line, string.Empty));

    private static bool HasArtifactEvidence(bool workerResultPresent, bool hasCommittedChanges) =>
        workerResultPresent || hasCommittedChanges;

    private static bool HasDispatchCompletionEvidence(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges)
    {
        if (hasCommittedChanges)
        {
            return true;
        }

        if (HasPassingVerificationEvidence(verification))
        {
            return true;
        }

        if (HasWorkerResultDeferral(verification))
        {
            return true;
        }

        if (workerResultPresent)
        {
            return CanCompleteWithoutChangeEvidence(task, verification) &&
                HasPopulatedStandardOutput(verification);
        }

        return !IsTransientEmptyOutputDispatchFlake(verification);
    }

    private static bool CanCompleteWithoutChangeEvidence(TaskSpec task, TaskVerificationRecord verification) =>
        DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var capability) && capability switch
        {
            DispatchRoleOutputCapability.ReadOnly => true,
            DispatchRoleOutputCapability.VerificationOnly => HasAcceptableStructuredTestsForCompletion(verification),
            _ => false
        };

    private static bool HasPassingVerificationEvidence(TaskVerificationRecord verification) =>
        HasVerificationEvidence(verification) &&
        !HasFailingTestsForCompatibility(verification);

    private static bool HasPopulatedStandardOutput(TaskVerificationRecord verification) =>
        verification.HeartbeatStandardOutputBytes > 0 ||
        !string.IsNullOrWhiteSpace(verification.StandardOutput) ||
        HasStandardOutputFileBytes(verification);

    private static bool HasSubstantiveWorkerEvidence(bool workerResultPresent, bool hasCommittedChanges) =>
        workerResultPresent && hasCommittedChanges;

    private static bool IsRetryRoundWithoutCommitOrDeferral(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        verification.Succeeded &&
        workerResultPresent &&
        !hasCommittedChanges &&
        task.RequiredRole == AgentRole.Developer &&
        (task.CriterionRetryCount > 0 || task.CriterionRetryFeedback.Count > 0) &&
        !HasWorkerResultDeferral(verification);

    private static bool IsRetryRoundVerifiedNoNewCommit(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        verification.Succeeded &&
        workerResultPresent &&
        !hasCommittedChanges &&
        task.RequiredRole == AgentRole.Developer &&
        (task.CriterionRetryCount > 0 || task.CriterionRetryFeedback.Count > 0) &&
        HasVerifiedNoNewCommitWorkerResult(task, verification);

    private static bool IsDeveloperVerifiedNoChangeRound(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges)
    {
        var dispatch = task.LastDispatch;
        var integrationSatisfied = dispatch?.PreDispatchIntegrationReceipt?.Matches(
            dispatch.GoalId,
            task,
            dispatch.BaseCommit) == true;
        var earlyConvergenceSatisfied = dispatch?.ContextPackageReceipt?.HasEarlyConvergenceEvidenceFor(
            dispatch.BaseCommit) == true;
        if (task.RequiredRole != AgentRole.Developer ||
            hasCommittedChanges ||
            !workerResultPresent ||
            !WasRedispatchedByAnyRoute(task) ||
            string.IsNullOrWhiteSpace(dispatch?.BaseCommit) ||
            (!integrationSatisfied && !earlyConvergenceSatisfied) ||
            !HasPopulatedStandardOutput(verification) ||
            !WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockersStatus) ||
            blockersStatus != WorkerResultBlockers.BlockersStatus.None ||
            WorkerResultBlockers.TryFindBlocker(verification, out _) ||
            !WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) ||
            testsStatus != WorkerResultBlockers.TestsStatus.Pass ||
            HasStructuredFailingTests(verification) ||
            (integrationSatisfied &&
             (!WorkerResultBlockers.TryFindTests(verification, out var tests) ||
              !FreshVerificationEvidence.HasNonZeroTestCount(tests))))
        {
            return false;
        }

        if (integrationSatisfied && verification.Succeeded)
        {
            return true;
        }

        return DispatchRejectionDiagnosticMarker.TryParse(
                verification.StandardError,
                out var verificationRecognized,
                out var reason,
                out var postDispatchCommits,
                out var changedPaths) &&
            verificationRecognized &&
            string.Equals(reason, DispatchRejectionDiagnosticMarker.NoChangeEvidence, StringComparison.Ordinal) &&
            postDispatchCommits == 0 &&
            (string.IsNullOrWhiteSpace(changedPaths) ||
             string.Equals(changedPaths, "none", StringComparison.OrdinalIgnoreCase)) &&
            TryGetOrchestratorAuthoredFailure(verification, out var authoredFailure) &&
            string.Equals(
                authoredFailure.Rule.Token,
                TaskOutcomeRules.RequiredFileChangeEvidenceMissing.Token,
                StringComparison.Ordinal);
    }

    private static bool IsVerifiedNoChangeRoundWithRecognisedEvidence(
        OrchestratorAuthoredFailure authoredFailure,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        !hasCommittedChanges &&
        workerResultPresent &&
        HasPopulatedStandardOutput(verification) &&
        string.Equals(authoredFailure.Rule.Token, TaskOutcomeRules.RequiredFileChangeEvidenceMissing.Token, StringComparison.Ordinal) &&
        DispatchRejectionDiagnosticMarker.TryParse(verification.StandardError, out var verificationRecognized, out _, out _, out _) &&
        verificationRecognized &&
        WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockersStatus) &&
        blockersStatus == WorkerResultBlockers.BlockersStatus.None &&
        !WorkerResultBlockers.TryFindBlocker(verification, out _) &&
        WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) &&
        testsStatus == WorkerResultBlockers.TestsStatus.Pass &&
        !HasStructuredFailingTests(verification);

    // The verified-no-change allowance is gated on re-dispatch, not on the reason for it. Operator
    // recovery and upstream bounces record LatestRetryAt without touching criterion-retry state.
    private static bool WasRedispatchedByAnyRoute(TaskSpec task) =>
        task.LatestRetryAt is not null ||
        task.CriterionRetryCount > 0 ||
        task.CriterionRetryFeedback.Count > 0;

    private static bool HasVerifiedNoNewCommitWorkerResult(TaskSpec task, TaskVerificationRecord verification) =>
        HasPopulatedStandardOutput(verification) &&
        WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockersStatus) &&
        blockersStatus == WorkerResultBlockers.BlockersStatus.None &&
        WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) &&
        testsStatus == WorkerResultBlockers.TestsStatus.Pass &&
        !WorkerResultBlockers.TryFindBlocker(verification, out _) &&
        !HasStructuredFailingTests(verification) &&
        TryGetKnownWorkerResultCommitSha(task, verification, out _);

    internal static bool TryGetKnownWorkerResultCommitSha(
        TaskSpec task,
        TaskVerificationRecord verification,
        out string commitSha)
    {
        commitSha = string.Empty;
        if (!DispatchClassifierReceipt.TryGetWorkerResultFieldValue(verification, "commit", out var commitValue) ||
            DispatchClassifierReceipt.IsNoWorkerResultBlockersValue(commitValue))
        {
            return false;
        }

        var match = CommitShaPattern.Match(commitValue);
        if (!match.Success)
        {
            return false;
        }

        var candidate = match.Value;
        if (MatchesKnownDispatchCommit(task.LastDispatch?.BaseCommit, candidate) ||
            MatchesKnownDispatchCommit(task.LastDispatch?.ResultCommit, candidate))
        {
            commitSha = candidate;
            return true;
        }

        return false;
    }

    private static bool MatchesKnownDispatchCommit(string? knownCommit, string candidate)
    {
        if (string.IsNullOrWhiteSpace(knownCommit))
        {
            return false;
        }

        var known = knownCommit.Trim();
        return known.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(known, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasWorkerResultDeferral(TaskVerificationRecord verification) =>
        HasWorkerResultDeferral(
            DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: false));

    private static bool HasWorkerResultDeferral(IEnumerable<string> evidenceLines)
    {
        var retainedLines = evidenceLines.ToArray();
        return (WorkerResultBlockers.TryGetTestsStatus(string.Join('\n', retainedLines), out var testsStatus) &&
                testsStatus == WorkerResultBlockers.TestsStatus.Deferred) ||
            (DispatchClassifierReceipt.TryGetWorkerResultFieldValue(retainedLines, "deferrals", out var deferrals) &&
             !DispatchClassifierReceipt.IsNoWorkerResultBlockersValue(deferrals));
    }

    private static bool HasGreenCommittedWorkerResultEvidence(
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        workerResultPresent &&
        hasCommittedChanges &&
        HasPopulatedStandardOutput(verification) &&
        WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockersStatus) &&
        blockersStatus == WorkerResultBlockers.BlockersStatus.None &&
        WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) &&
        testsStatus is WorkerResultBlockers.TestsStatus.Pass or WorkerResultBlockers.TestsStatus.NotRun &&
        !WorkerResultBlockers.TryFindBlocker(verification, out _) &&
        !HasStructuredFailingTests(verification);

    private static bool HasStructuredFailingTests(TaskVerificationRecord verification) =>
        WorkerResultBlockers.TryGetTestsStatus(verification, out var status) &&
        status == WorkerResultBlockers.TestsStatus.Fail;

    private static bool HasFailingTestsForCompatibility(TaskVerificationRecord verification) =>
        HasStructuredFailingTests(verification) ||
        (!WorkerResultBlockers.TryGetTestsStatus(verification, out _) &&
         WorkerResultBlockers.TryFindFailingTests(verification, out _));

    private static bool HasAcceptableStructuredTestsForCompletion(TaskVerificationRecord verification) =>
        WorkerResultBlockers.TryGetTestsStatus(verification, out var status) &&
        status is WorkerResultBlockers.TestsStatus.Pass or WorkerResultBlockers.TestsStatus.NotRun;

    private static bool HasWorkerEvidenceThatOutranksSubscriptionLimit(
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        WorkerResultBlockers.TryFindBlocker(verification, out _) ||
        HasSubstantiveWorkerEvidence(workerResultPresent, hasCommittedChanges);

    // A Low-IL (sandboxed) worker can do valid work but be structurally unable to self-commit — it
    // cannot write .git (Medium integrity), and therefore cannot run the git-dependent suite to
    // produce verification evidence. That failure is benign: the edits are real, only the commit was
    // blocked by OS confinement. We recognise it (git permission/index.lock signature + the worker
    // having produced useful work) so the orchestrator can commit the edits at Medium and let the
    // acceptance suite — which always runs before any merge to main — be the authoritative gate.
    public static bool IsSandboxCommitBlockedFailure(TaskSpec task, TaskVerificationRecord verification)
    {
        if (verification.Succeeded || !CanReceiveSandboxCommitBlockedOutcome(task.RequiredRole))
        {
            return false;
        }

        if (HasSandboxCommitBlockedEvidence(verification.StandardOutput, verification.StandardError))
        {
            return true;
        }

        return HasSandboxCommitBlockedEvidence(
            verification,
            includeRetainedText: false);
    }

    public static bool IsSandboxCommitBlockedFailure(
        AgentRole role,
        int exitCode,
        string standardOutput,
        string standardError) =>
        exitCode != 0 &&
        CanReceiveSandboxCommitBlockedOutcome(role) &&
        HasSandboxCommitBlockedEvidence(standardOutput, standardError);

    private static bool CanReceiveSandboxCommitBlockedOutcome(AgentRole role) =>
        DispatchRoleOutputCapabilities.TryGet(role, out var capability) &&
        capability != DispatchRoleOutputCapability.ReadOnly;

    private static bool HasSandboxCommitBlockedEvidence(string standardOutput, string standardError)
    {
        var output = $"{standardOutput}\n{standardError}";
        var commitBlocked =
            output.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("blocked on committing", StringComparison.OrdinalIgnoreCase) ||
            (output.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
             output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));

        // The worker must have reported doing real work — the WORKER_RESULT contract marker, or any
        // other useful-output signal. The downstream auto-verify additionally requires committed
        // changes against main and the acceptance suite, so this is the lightest of several gates.
        var didUsefulWork =
            standardOutput.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase) ||
            ProviderFailureSignals.HasUsefulPreWorkOutput(standardOutput);

        return commitBlocked && didUsefulWork;
    }

    private static bool HasSandboxCommitBlockedEvidence(TaskVerificationRecord verification, bool includeRetainedText)
    {
        var commitBlocked = false;
        var didUsefulWork = false;
        foreach (var line in DispatchEvidenceSummaries.EnumerateEvidenceLines(
                     verification,
                     includeStandardOutput: true,
                     includeStandardError: true,
                     includeRetainedText: includeRetainedText,
                     includeArtifactText: true))
        {
            commitBlocked |=
                line.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("blocked on committing", StringComparison.OrdinalIgnoreCase) ||
                (line.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
                 line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));

            didUsefulWork |=
                line.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase) ||
                ProviderFailureSignals.HasUsefulPreWorkOutput(line);

            if (commitBlocked && didUsefulWork)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasRecoverableSubscriptionLimitHistory(TaskSpec task)
    {
        return task.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Pending &&
            task.LastVerification is null &&
            task.VerificationHistory.LastOrDefault() is { Succeeded: false } latest &&
            IsRecoverableSubscriptionLimitFailure(latest);
    }

    public static int CountRecoverableSubscriptionLimitFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableSubscriptionLimitFailure(verification));
    }

    internal static TaskDispatchRecord? FindDispatchForVerification(
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        if (verification.DispatchStartedAt is { } dispatchStartedAt)
        {
            var exact = task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt == dispatchStartedAt);
            if (exact is not null)
            {
                return exact;
            }
        }

        return task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt <= verification.CompletedAt);
    }

    internal static ProviderBindingKey BuildProviderBindingKey(string providerName, string? credentialBinding)
    {
        var provider = string.IsNullOrWhiteSpace(providerName) ? "unknown" : providerName.Trim();
        if (string.IsNullOrWhiteSpace(credentialBinding))
        {
            return new ProviderBindingKey($"{provider.ToLowerInvariant()}::<provider-default>", "provider-only");
        }

        return new ProviderBindingKey(
            $"{provider.ToLowerInvariant()}::{credentialBinding.Trim().ToLowerInvariant()}",
            "provider+credential");
    }

    internal sealed record ProviderBindingKey(string Key, string Scope);

    internal static bool TryGetRecoverableSubscriptionLimitLine(TaskVerificationRecord verification, out string line) =>
        ProviderLimitEvidenceParser.TryGetEvidenceLine(
            DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true),
            out line) || TryGetCliRefusalLimitLine(verification, out line);

    internal static bool TryGetCliAbsoluteResetRetryAfter(
        string line, DateTimeOffset completedAt, out DateTimeOffset retryAfter) =>
        TryGetAbsoluteResetRetryAfter(line, completedAt, out retryAfter);

    public static bool IsSubscriptionProviderCliDispatch(TaskSpec task) =>
        task.LastDispatch is { } dispatch && IsSubscriptionProviderCliDispatch(dispatch);

    internal static bool IsSubscriptionProviderCliDispatch(TaskDispatchRecord dispatch)
    {
        if (dispatch.WorkerProviderKind is
            ProviderKind.OpenAICodexCli or
            ProviderKind.AnthropicClaudeCli or
            ProviderKind.OpenAICodexLuna or
            ProviderKind.OpenAICodexOssCli or
            ProviderKind.OllamaQwenCodeCli or
            ProviderKind.XaiGrokCli)
        {
            return true;
        }

        return dispatch.WorkerName.Equals("codex-cli", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("claude-cli", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(LunaLaneNames.NormalizeProfileName(dispatch.WorkerName), LunaLaneNames.LunaProfileName, StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("codex-oss-cli", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("qwen-code-cli", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("grok-cli", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWorkerResultOpener(string line)
    {
        var normalized = NormalizeWorkerResultMarker(line).TrimEnd(':').Trim();
        return string.Equals(normalized, "WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWorkerResultEndMarker(string line)
    {
        var normalized = NormalizeWorkerResultMarker(line);
        return string.Equals(normalized, "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeWorkerResultMarker(string text)
    {
        var trimmed = text.Trim();
        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var ch in trimmed)
        {
            if (ch is not ('#' or '*' or '`'))
            {
                buffer[length++] = ch;
            }
        }

        return new string(buffer, 0, length).Trim();
    }

    internal static bool IsDirtyDispatchGuardFailure(TaskVerificationRecord verification)
    {
        return verification.StandardError.Contains(
            "Developer/Tester dispatch exited 0 but left the worktree dirty",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ExtractChangedFiles(string standardError)
    {
        foreach (var rawLine in standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = "status_short=";
            var markerIndex = rawLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var value = rawLine[(markerIndex + marker.Length)..].Trim();
            if (value.EndsWith(".", StringComparison.Ordinal))
            {
                value = value[..^1];
            }

            var entries = value
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => !string.Equals(entry, "clean", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry, "unavailable", StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToArray();
            return entries.Length == 0 ? ["unavailable"] : entries;
        }

        return ["unavailable"];
    }

    private static string[] ExtractVerificationEvidence(string standardOutput, string standardError)
    {
        var lines = $"{standardOutput}\n{standardError}"
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("Developer/Tester dispatch exited 0 but left the worktree dirty", StringComparison.OrdinalIgnoreCase))
            .Where(line => HasVerificationEvidence(line, string.Empty))
            .Take(3)
            .ToArray();

        return lines;
    }
}

public sealed record DirtyDispatchRecovery(
    string Label,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> VerificationEvidence,
    string WorkingDirectory)
{
    public bool HasUsefulVerification => VerificationEvidence.Count > 0;

    public static DirtyDispatchRecovery None { get; } = new(
        string.Empty,
        [],
        [],
        string.Empty);
}
