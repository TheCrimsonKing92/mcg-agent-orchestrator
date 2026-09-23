using System.Text;
using System.Text.Json;
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
    ProviderInterruption
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
    DateTimeOffset RetryAfter);

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
    public const string WrapperProcessExitFailure = "wrapper-process-exit-failure";
    public const string RequiredFileChangeEvidenceMissing = "required-file-change-evidence-missing";
    public const string WorktreeInspectionFailed = "worktree-inspection-failed";

    public static string Format(string code) => $"{Prefix} {code}";
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

public static class DispatchFailureClassifier
{
    private static readonly TimeSpan BareClockRetryStalenessTolerance = TimeSpan.FromHours(1);

    private enum ProviderTurnRecordKind
    {
        Other,
        Completed,
        Failed
    }

    // Codex emits these records under `codex exec --json`. The configured claude-cli and grok-cli
    // profiles emit plain text, so their failure vocabularies remain named gaps rather than guesses.
    private static readonly IReadOnlyDictionary<string, ProviderTurnRecordKind> ProviderTurnRecordVocabulary =
        new Dictionary<string, ProviderTurnRecordKind>(StringComparer.Ordinal)
        {
            ["turn.completed"] = ProviderTurnRecordKind.Completed,
            ["turn.failed"] = ProviderTurnRecordKind.Failed,
            ["error"] = ProviderTurnRecordKind.Failed
        };

    private sealed record OrchestratorAuthoredFailure(TaskOutcomeRule Rule, string Description);

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
            [DispatchFailureDiagnosticMarker.WrapperProcessExitFailure] = new(
                TaskOutcomeRules.WrapperProcessExitFailure,
                "Wrapper process exited nonzero after the selected child succeeded, without usable completion evidence."),
            [DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing] = new(
                TaskOutcomeRules.RequiredFileChangeEvidenceMissing,
                "Developer/Tester dispatch did not produce required relevant file-change evidence."),
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
            ProviderFailureKind.RateLimit => WithClassifierReceipt(
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
            ProviderFailureKind.Connectivity => WithClassifierReceipt(
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
            ProviderFailureKind.Sandbox1312 => WithClassifierReceipt(
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
            _ => WithClassifierReceipt(
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

    private static readonly Regex CodexCliDiagnosticPrefix = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?\s+ERROR\s+codex(?:[_:\w.-]*)?\b",
        RegexOptions.CultureInvariant);
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
    private static readonly Regex TryAgainInPattern = new(
        @"\btry again in\s+(?<value>\d+)\s*(?<unit>ms|milliseconds?|s|sec|secs|seconds?|m|mins?|minutes?|h|hrs?|hours?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ResetsInPattern = new(
        @"\bresets?\s+in\s*:?\s*(?:(?<hours>\d+)\s*h(?:ours?)?)?\s*(?:(?<minutes>\d+)\s*m(?:in(?:ute)?s?)?)?\s*(?:(?<seconds>\d+)\s*s(?:ec(?:ond)?s?)?)?",
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

        if (TryGetProviderAuthenticationLine(verification, out _) ||
            TryGetProviderConnectivityLine(verification, out _))
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
        HasWorkerResultDeferral(SplitEvidenceLines(standardOutput));

    public static bool IsTransientEmptyOutputDispatchFlake(TaskVerificationRecord verification)
    {
        if (IsPreflightFailure(verification))
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
            IsPreflightFailure(verification) ||
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

    private static bool HasStandardOutputFileBytes(TaskVerificationRecord verification)
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

    private static bool IsRunnerBookkeepingLine(string line) =>
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
    {
        if (IsProviderInterruptionFailure(verification))
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
                BuildEvidenceSummary(verification)));
        }

        providerFailureKind = providerFailureKind == ProviderFailureKind.Unknown
            ? verification.ProviderFailureKind
            : providerFailureKind;
        workerResultPresent = workerResultPresent || verification.WorkerResultPresent;
        hasCommittedChanges = hasCommittedChanges || verification.HasCommittedChanges || HasDispatchResultCommitEvidence(task);
        var exitCode = verification.ExitCode;
        var hasZeroByteOutput = HasZeroByteStandardOutput(verification);

        if (task.RequiredRole == AgentRole.Tester &&
            workerResultPresent &&
            !IsDirtyDispatchGuardFailure(verification) &&
            WorkerResultBlockers.TryGetTestsStatus(verification, out var inconclusiveStatus) &&
            inconclusiveStatus == WorkerResultBlockers.TestsStatus.Inconclusive)
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

        if (WorkerResultBlockers.TryFindTesterWorkerResultBlocker(task, verification, out var testerBlocker))
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
                BuildProviderModelRejectionEvidenceSummary(verification)));
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
                BuildEvidenceSummary(verification)));
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
                BuildVerifiedNoNewCommitEvidenceSummary(task, verification)));
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
                BuildVerifiedNoChangeRoundEvidenceSummary(task, verification)));
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
                BuildEvidenceSummary(verification)));
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
                BuildEvidenceSummary(verification)));
        }

        if (providerFailureKind == ProviderFailureKind.Sandbox1312)
        {
            var providerOutcome = ClassifyProviderFailure(
                providerFailureKind,
                exitCode,
                hasZeroByteOutput,
                BuildEvidenceSummary(verification));
            return BuildOutcome(
                TaskOutcomeRules.ProviderSandboxLaunch1312,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                providerOutcome);
        }

        if (IsRecoverableProviderAuthenticationFailure(verification))
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
                BuildProviderAuthenticationEvidenceSummary(verification)));
        }

        if (IsRecoverableProviderConnectivityFailure(verification))
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
                BuildProviderConnectivityEvidenceSummary(verification)));
        }

        if (IsSubscriptionProviderCliDispatch(task) &&
            IsRecoverableSubscriptionLimitFailure(verification))
        {
            TimeSpan? retryAfter = null;
            if (TryGetSubscriptionLimitRetryAfter(verification, out var retryAfterAbs))
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
                BuildRecoverableSubscriptionLimitEvidenceSummary(verification)));
        }

        if (IsPreflightFailure(verification))
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
                BuildPreflightFailureEvidenceSummary(verification)));
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
                BuildEvidenceSummary(verification)));
        }

        if (IsProviderNeutralProgressStallFailure(verification))
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
                BuildEvidenceSummary(verification)));
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
                BuildRecoverableSubscriptionLimitEvidenceSummary(verification)));
        }

        if (IsRecoverableProviderModelRejectionFailure(verification, task.RequiredRole))
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
                BuildEvidenceSummary(verification)));
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
                verifiedNoChange ? RecoveryRecommendation.None : RecoveryRecommendation.OperatorNeeded,
                verifiedNoChange
                    ? BuildVerifiedNoChangeRoundEvidenceSummary(task, verification)
                    : BuildOrchestratorAuthoredFailureEvidenceSummary(authoredFailure.Description, verification)));
        }

        if (verification.ExitCode != 0 && HasScriptingFailureEvidence(verification))
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
                BuildRealFailureEvidenceSummary(verification)));
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
            BuildUnknownFailureEvidenceSummary(verification)));
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
        string description,
        TaskVerificationRecord verification)
    {
        var substantiveLines = GetSubstantiveStandardErrorLines(verification)
            .TakeLast(3)
            .ToArray();
        return substantiveLines.Length == 0
            ? description
            : $"{description} diagnostic stderr-tail: {TruncateEvidence(string.Join(" | ", substantiveLines))}";
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
            ClassifierReceipt = AppendEvidenceReceipt(
                BuildClassifierReceipt(rule, outcomeClass, task, verification, workerResultPresent, hasCommittedChanges, outcome.Kind),
                outcome)
        };
    }

    private static DispatchOutcome WithClassifierReceipt(TaskOutcomeRule rule, DispatchOutcome outcome, int exitCode) =>
        (outcome with { OutcomeClass = rule.Class }) with
        {
            ClassifierReceipt = AppendEvidenceReceipt(
                $"CLASSIFIER rule={rule.Token}; outcome_class={TaskOutcomeClassifier.FormatClass(rule.Class)}; " +
                $"exit_code={exitCode}; exit_artifact=direct-provider-failure; " +
                "stdout_bytes=unknown; stderr_bytes=unknown; heartbeat_stdout_bytes=unknown; " +
                $"worker_result=absent; commit=none; verdict={outcome.Kind}",
                outcome)
        };

    private static string AppendEvidenceReceipt(string receipt, DispatchOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.EvidenceSummary))
        {
            return receipt;
        }

        return $"{receipt}; evidence={SanitizeReceiptValue(outcome.EvidenceSummary, maxLength: 240)}";
    }

    private static string BuildClassifierReceipt(
        TaskOutcomeRule rule,
        TaskOutcomeClass outcomeClass,
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges,
        DispatchOutcomeKind verdict)
    {
        var stdoutBytes = GetOutputByteCount(verification.StandardOutputPath, verification.StandardOutput);
        var stderrBytes = GetOutputByteCount(verification.StandardErrorPath, verification.StandardError);
        var heartbeat = verification.HeartbeatStandardOutputBytes is { } heartbeatBytes
            ? heartbeatBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        var workerResult = DescribeWorkerResult(verification, workerResultPresent);
        var commitProvenance = DescribeCommitProvenance(task, verification, hasCommittedChanges);
        var duration = verification.DispatchStartedAt is { } startedAt
            ? Math.Max(0, (long)(verification.CompletedAt - startedAt).TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

        var childExitCode = verification.ChildExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var rootExitCode = (verification.ObservedRootExitCode ?? verification.ExitCode)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reconciliationOrigin = string.IsNullOrWhiteSpace(verification.ReconciliationOriginRule)
            ? string.Empty
            : $"; origin_rule={SanitizeReceiptValue(verification.ReconciliationOriginRule)}";

        return $"CLASSIFIER rule={rule.Token}; outcome_class={TaskOutcomeClassifier.FormatClass(outcomeClass)}{reconciliationOrigin}; " +
            $"exit_code={verification.ExitCode}; root_exit_code={rootExitCode}; " +
            $"child_exit_code={childExitCode}; exit_artifact=verification-record; " +
            $"stdout_bytes={stdoutBytes}; stderr_bytes={stderrBytes}; heartbeat_stdout_bytes={heartbeat}; " +
            $"duration_ms={duration}; worker_result={workerResult}; commit={commitProvenance}; verdict={verdict}";
    }

    private static long GetOutputByteCount(string? path, string output)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        return Encoding.UTF8.GetByteCount(output);
    }

    private static string DescribeWorkerResult(TaskVerificationRecord verification, bool workerResultPresent)
    {
        if (TryGetWorkerResultBlockersValue(verification, out var blockers))
        {
            return $"present(blockers={SanitizeReceiptValue(blockers)})";
        }

        if (workerResultPresent ||
            verification.StandardOutput.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase) ||
            verification.StandardError.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase))
        {
            return WorkerResultBlockers.TryFindBlocker(verification, out var blocker)
                ? $"present(blockers={SanitizeReceiptValue(blocker)})"
                : "present(blockers=absent)";
        }

        return "absent";
    }

    private static bool TryGetWorkerResultBlockersValue(TaskVerificationRecord verification, out string blockers)
    {
        return TryGetWorkerResultFieldValue(verification, "blockers", out blockers);
    }

    private static bool TryGetWorkerResultFieldValue(
        TaskVerificationRecord verification,
        string fieldName,
        out string value) =>
        TryGetWorkerResultFieldValue(
            EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true),
            fieldName,
            out value);

    private static bool TryGetWorkerResultFieldValue(
        IEnumerable<string> evidenceLines,
        string fieldName,
        out string value)
    {
        value = string.Empty;
        var inBlock = false;
        string? latestValue = null;

        foreach (var rawLine in evidenceLines)
        {
            var line = rawLine.Trim();
            if (IsWorkerResultOpener(line))
            {
                inBlock = true;
                latestValue = null;
                continue;
            }

            if (IsWorkerResultEndMarker(line))
            {
                if (inBlock && latestValue is not null)
                {
                    value = latestValue;
                    return true;
                }

                inBlock = false;
                continue;
            }

            if (!inBlock)
            {
                continue;
            }

            var sep = line.IndexOf(':', StringComparison.Ordinal);
            if (sep <= 0)
            {
                continue;
            }

            var key = NormalizeWorkerResultMarker(line[..sep]).TrimStart('-', ' ').Trim();
            if (string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                latestValue = line[(sep + 1)..].Trim();
            }
        }

        if (inBlock && latestValue is not null)
        {
            value = latestValue;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateEvidenceLines(
        TaskVerificationRecord verification,
        bool includeStandardOutput,
        bool includeStandardError,
        bool includeRetainedText = true,
        bool includeArtifactText = true)
    {
        if (includeStandardOutput)
        {
            if (includeRetainedText)
            {
                foreach (var line in SplitEvidenceLines(verification.StandardOutput))
                {
                    yield return line;
                }
            }

            if (includeArtifactText)
            {
                foreach (var line in ReadEvidenceFileLines(verification.StandardOutputPath))
                {
                    yield return line;
                }
            }
        }

        if (includeStandardError)
        {
            if (includeRetainedText)
            {
                foreach (var line in SplitEvidenceLines(verification.StandardError))
                {
                    yield return line;
                }
            }

            if (includeArtifactText)
            {
                foreach (var line in ReadEvidenceFileLines(verification.StandardErrorPath))
                {
                    yield return line;
                }
            }
        }
    }

    private static IEnumerable<string> ReadEvidenceFileLines(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            yield break;
        }

        StreamReader reader;
        try
        {
            reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        using (reader)
        {
            while (true)
            {
                string? line;
                try
                {
                    line = reader.ReadLine();
                }
                catch (IOException)
                {
                    yield break;
                }
                catch (UnauthorizedAccessException)
                {
                    yield break;
                }

                if (line is null)
                {
                    yield break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return line.TrimEnd();
                }
            }
        }
    }

    private static string DescribeCommitProvenance(TaskSpec task, TaskVerificationRecord verification, bool hasCommittedChanges)
    {
        if (HasDispatchResultCommitEvidence(task))
        {
            return "orchestrator";
        }

        return hasCommittedChanges || verification.HasCommittedChanges ? "worker" : "none";
    }

    private static bool IsNoWorkerResultBlockersValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (value.StartsWith('<') && value.EndsWith('>'))
        {
            return true;
        }

        return value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none-", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none.", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none:", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeReceiptValue(string value, int maxLength = 80)
    {
        var sanitized = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace(";", ",", StringComparison.Ordinal)
            .Trim();

        return sanitized.Length > maxLength ? sanitized[..maxLength] : sanitized;
    }

    private static bool HasDispatchResultCommitEvidence(TaskSpec task)
    {
        if (task.LastDispatch is not { } dispatch ||
            string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            string.IsNullOrWhiteSpace(dispatch.ResultCommit))
        {
            return false;
        }

        return !string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEvidenceSummary(TaskVerificationRecord verification)
    {
        if (IsDirtyDispatchGuardFailure(verification))
        {
            return "dirty-dispatch-recovery";
        }

        if (TryGetPreflightFailureEvidenceLine(verification, out var preflightLine))
        {
            return BuildPreflightFailureEvidenceSummary(preflightLine);
        }

        if (TryGetProviderAuthenticationLine(verification, out var providerAuthLine))
        {
            return TruncateEvidence(providerAuthLine);
        }

        if (TryGetProviderConnectivityLine(verification, out var providerConnectivityLine))
        {
            return TruncateEvidence(providerConnectivityLine);
        }

        if (TryGetRecoverableSubscriptionLimitLine(verification, out var providerLimitLine))
        {
            return TruncateEvidence(providerLimitLine);
        }

        if (!HasVerificationEvidence(verification))
        {
            return string.Empty;
        }

        foreach (var rawLine in EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true))
        {
            if (HasVerificationEvidence(rawLine, string.Empty))
            {
                return TruncateEvidence(rawLine);
            }
        }

        return "Verification evidence found in output.";
    }

    private static string TruncateEvidence(string evidence) =>
        evidence.Length > 200 ? evidence[..200] : evidence;

    private static string BuildRecoverableSubscriptionLimitEvidenceSummary(TaskVerificationRecord verification) =>
        TryGetRecoverableSubscriptionLimitLine(verification, out var line)
            ? TruncateEvidence(line)
            : BuildEvidenceSummary(verification);

    private static string BuildUnknownFailureEvidenceSummary(TaskVerificationRecord verification)
    {
        var substantiveLines = GetSubstantiveStandardErrorLines(verification)
            .TakeLast(3)
            .ToArray();

        return substantiveLines.Length == 0
            ? BuildEvidenceSummary(verification)
            : $"unknown-failure stderr-tail: {TruncateEvidence(string.Join(" | ", substantiveLines))}";
    }

    private static string BuildRealFailureEvidenceSummary(TaskVerificationRecord verification)
    {
        var substantiveLines = GetSubstantiveStandardErrorLines(verification)
            .TakeLast(3)
            .ToArray();

        return substantiveLines.Length == 0
            ? BuildEvidenceSummary(verification)
            : $"real-failure stderr-tail: {TruncateEvidence(string.Join(" | ", substantiveLines))}";
    }

    private static bool HasScriptingFailureEvidence(TaskVerificationRecord verification) =>
        GetSubstantiveStandardErrorLines(verification).Any(IsScriptingFailureLine);

    private static bool IsScriptingFailureLine(string line) =>
        line.Contains("ParserError", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("NativeCommandError", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("is not recognized as the name of a cmdlet", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("The string is missing the terminator", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> GetStandardErrorEvidenceLines(TaskVerificationRecord verification)
    {
        foreach (var line in EnumerateEvidenceLines(verification, includeStandardOutput: false, includeStandardError: true))
        {
            yield return line;
        }
    }

    private static IEnumerable<string> GetSubstantiveStandardErrorLines(TaskVerificationRecord verification) =>
        GetStandardErrorEvidenceLines(verification).Where(line => !IsRunnerBookkeepingLine(line));

    private static string BuildVerifiedNoNewCommitEvidenceSummary(TaskSpec task, TaskVerificationRecord verification)
    {
        var commitEvidence = TryGetKnownWorkerResultCommitSha(task, verification, out var commitSha)
            ? $"commit {commitSha}"
            : "known commit";
        var testsEvidence = WorkerResultBlockers.TryFindTests(verification, out var tests)
            ? $"; tests: {TruncateEvidence(tests)}"
            : string.Empty;

        return $"verified-no-new-commit: {commitEvidence}{testsEvidence}";
    }

    private static string BuildVerifiedNoChangeRoundEvidenceSummary(TaskSpec task, TaskVerificationRecord verification)
    {
        var testsEvidence = WorkerResultBlockers.TryFindTests(verification, out var tests)
            ? $"; tests: {TruncateEvidence(tests)}"
            : string.Empty;

        var integration = task.LastDispatch?.PreDispatchIntegrationReceipt;
        if (integration?.Matches(task.LastDispatch?.GoalId, task, task.LastDispatch?.BaseCommit) == true)
        {
            return $"pre-dispatch integration satisfied retry: original {integration.OriginalCandidateSha}; " +
                $"main {integration.IntegratedMainSha}; candidate {integration.ResultingCandidateSha}{testsEvidence}";
        }

        return $"verified-no-change-round: candidate {task.LastDispatch?.BaseCommit ?? "unknown"}{testsEvidence}";
    }

    private static string BuildProviderAuthenticationEvidenceSummary(TaskVerificationRecord verification) =>
        TryGetProviderAuthenticationLine(verification, out var line)
            ? $"provider-authentication: {TruncateEvidence(line)}; remediation=codex login / provider re-auth"
            : "provider-authentication; remediation=codex login / provider re-auth";

    private static string BuildProviderConnectivityEvidenceSummary(TaskVerificationRecord verification) =>
        TryGetProviderConnectivityLine(verification, out var line)
            ? TruncateEvidence(line)
            : BuildEvidenceSummary(verification);

    private static string BuildProviderModelRejectionEvidenceSummary(TaskVerificationRecord verification) =>
        TryGetProviderModelRejectionLine(verification, out var line)
            ? TruncateEvidence(line)
            : BuildEvidenceSummary(verification);

    public static bool IsPreflightFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded ||
            !string.IsNullOrWhiteSpace(verification.StandardOutput) ||
            HasStandardOutputFileBytes(verification))
        {
            return false;
        }

        return TryGetPreflightFailureEvidenceLine(verification, out _);
    }

    private static string BuildPreflightFailureEvidenceSummary(TaskVerificationRecord verification) =>
        TryGetPreflightFailureEvidenceLine(verification, out var line)
            ? BuildPreflightFailureEvidenceSummary(line)
            : "sandbox-preflight-failure";

    private static string BuildPreflightFailureEvidenceSummary(string evidence) =>
        $"sandbox-preflight-failure: {TruncateEvidence(evidence)}";

    private static bool TryGetPreflightFailureEvidenceLine(TaskVerificationRecord verification, out string line)
    {
        foreach (var candidate in GetPreflightEvidenceLines(verification))
        {
            if (ContainsPreflightFailureText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static IEnumerable<string> GetPreflightEvidenceLines(TaskVerificationRecord verification)
    {
        foreach (var line in EnumerateEvidenceLines(verification, includeStandardOutput: false, includeStandardError: true))
        {
            yield return line;
        }
    }

    private static IEnumerable<string> SplitEvidenceLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line.TrimEnd();
            }
        }
    }

    private static bool ContainsPreflightFailureText(string text)
    {
        var hasPreflightContext =
            text.Contains("Low Integrity", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("integrity label", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("icacls", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CreateProcessAsUser", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("preflight", StringComparison.OrdinalIgnoreCase);

        var hasFailureEvidence =
            text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("1312", StringComparison.OrdinalIgnoreCase);

        return hasPreflightContext && hasFailureEvidence;
    }

    private static bool HasVerificationEvidence(string standardOutput, string standardError)
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

    private static bool HasVerificationEvidence(TaskVerificationRecord verification) =>
        EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true)
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

    private static bool TryGetKnownWorkerResultCommitSha(
        TaskSpec task,
        TaskVerificationRecord verification,
        out string commitSha)
    {
        commitSha = string.Empty;
        if (!TryGetWorkerResultFieldValue(verification, "commit", out var commitValue) ||
            IsNoWorkerResultBlockersValue(commitValue))
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
            EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: false));

    private static bool HasWorkerResultDeferral(IEnumerable<string> evidenceLines)
    {
        var retainedLines = evidenceLines.ToArray();
        return (WorkerResultBlockers.TryGetTestsStatus(string.Join('\n', retainedLines), out var testsStatus) &&
                testsStatus == WorkerResultBlockers.TestsStatus.Deferred) ||
            (TryGetWorkerResultFieldValue(retainedLines, "deferrals", out var deferrals) &&
             !IsNoWorkerResultBlockersValue(deferrals));
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
            HasUsefulPreWorkOutput(standardOutput);

        return commitBlocked && didUsefulWork;
    }

    private static bool HasSandboxCommitBlockedEvidence(TaskVerificationRecord verification, bool includeRetainedText)
    {
        var commitBlocked = false;
        var didUsefulWork = false;
        foreach (var line in EnumerateEvidenceLines(
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
                HasUsefulPreWorkOutput(line);

            if (commitBlocked && didUsefulWork)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasProviderNeutralProgressStallFailure(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Failed &&
            task.LastVerification is { Succeeded: false } latest &&
            IsProviderNeutralProgressStallFailure(latest);
    }

    public static bool IsProviderNeutralProgressStallFailure(TaskVerificationRecord verification)
    {
        var output = string.Join(
            Environment.NewLine,
            verification.StandardOutput,
            verification.StandardError);

        return output.Contains("no observable progress", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("stall timeout", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("heartbeat", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasRecoverableProviderConnectivityFailure(TaskSpec task)
    {
        if (!IsSubscriptionProviderCliDispatch(task))
        {
            return false;
        }

        if (task.Status == WorkTaskStatus.Failed &&
            task.LastVerification is { Succeeded: false } latest &&
            IsRecoverableProviderConnectivityFailure(latest))
        {
            return true;
        }

        return task.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Pending &&
            task.LastVerification is null &&
            task.VerificationHistory.LastOrDefault() is { Succeeded: false } historyLatest &&
            IsRecoverableProviderConnectivityFailure(historyLatest);
    }

    public static bool HasRecoverableProviderAuthenticationFailure(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Failed &&
            IsSubscriptionProviderCliDispatch(task) &&
            task.LastVerification is { Succeeded: false } latest &&
            IsRecoverableProviderAuthenticationFailure(latest);
    }

    public static bool HasRecoverableProviderModelRejectionFailure(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Failed &&
            IsSubscriptionProviderCliDispatch(task) &&
            task.LastVerification is { Succeeded: false } latest &&
            IsRecoverableProviderModelRejectionFailure(latest, task.RequiredRole);
    }

    public static int CountRecoverableProviderConnectivityFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableProviderConnectivityFailure(verification));
    }

    public static int CountConsecutiveProviderInterruptionFailures(TaskSpec task)
    {
        var count = 0;
        for (var index = task.VerificationHistory.Count - 1; index >= 0; index--)
        {
            if (!IsProviderInterruptionFailure(task.VerificationHistory[index]))
            {
                break;
            }

            count++;
        }

        return count;
    }

    public static bool IsProviderInterruptionFailure(TaskVerificationRecord verification)
    {
        var hasCompletedTurn = false;
        var hasFailedTurn = false;
        var inWorkerResultBlock = false;

        foreach (var rawLine in EnumerateEvidenceLines(
                     verification,
                     includeStandardOutput: true,
                     includeStandardError: false))
        {
            var line = rawLine.Trim();
            if (IsWorkerResultOpener(line))
            {
                inWorkerResultBlock = true;
                continue;
            }

            if (IsWorkerResultEndMarker(line))
            {
                inWorkerResultBlock = false;
                continue;
            }

            if (inWorkerResultBlock || !line.StartsWith('{'))
            {
                continue;
            }

            var recordKind = ClassifyProviderTurnRecord(line);
            hasCompletedTurn |= recordKind == ProviderTurnRecordKind.Completed;
            hasFailedTurn |= recordKind == ProviderTurnRecordKind.Failed;
        }

        // A fast exit with low CPU and memory is a useful operator diagnostic, but recorded event
        // structure is the authority. Resource timing and provider message text are not inputs.
        return !hasCompletedTurn && hasFailedTurn;
    }

    private static ProviderTurnRecordKind ClassifyProviderTurnRecord(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ProviderTurnRecordKind.Other;
            }

            var root = document.RootElement;
            if ((!root.TryGetProperty("type", out var discriminator) &&
                 !root.TryGetProperty("kind", out discriminator)) ||
                discriminator.ValueKind != JsonValueKind.String)
            {
                return ProviderTurnRecordKind.Other;
            }

            var value = discriminator.GetString();
            return value is not null && ProviderTurnRecordVocabulary.TryGetValue(value, out var recordKind)
                ? recordKind
                : ProviderTurnRecordKind.Other;
        }
        catch (JsonException)
        {
            return ProviderTurnRecordKind.Other;
        }
    }

    public static bool IsRecoverableProviderAuthenticationFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        return TryGetProviderAuthenticationLine(verification, out _) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static bool IsRecoverableProviderConnectivityFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        return (verification.ProviderFailureKind == ProviderFailureKind.Connectivity ||
            TryGetProviderConnectivityLine(verification, out _)) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static bool IsRecoverableProviderModelRejectionFailure(TaskVerificationRecord verification) =>
        IsRecoverableProviderModelRejectionFailure(verification, requiredRole: null);

    private static bool IsRecoverableProviderModelRejectionFailure(
        TaskVerificationRecord verification,
        AgentRole? requiredRole)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        return TryGetProviderModelRejectionLine(
                verification,
                ignoreAuthoritativePlannerContractLines: requiredRole == AgentRole.Planner,
                out _) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static int CountRecoverableProviderModelRejectionFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableProviderModelRejectionFailure(verification, task.RequiredRole));
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

    public static bool RequiresSubscriptionLimitReview(TaskSpec task)
    {
        var failureCount = CountRecoverableSubscriptionLimitFailures(task);
        return HasRecoverableSubscriptionLimitHistory(task) &&
            failureCount >= RecoverableSubscriptionLimitReviewThreshold &&
            task.SubscriptionLimitReviewedFailureCount < failureCount;
    }

    public static bool IsSubscriptionRetryDeferred(TaskSpec task, DateTimeOffset now, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (!TryGetSubscriptionLimitRetryAfter(task, out retryAfter))
        {
            return false;
        }

        return retryAfter > now;
    }

    public static bool TryGetProviderSubscriptionCooldown(
        Goal goal,
        TaskId candidateTaskId,
        string providerName,
        DateTimeOffset now,
        out ProviderSubscriptionCooldown cooldown)
    {
        cooldown = default!;
        foreach (var task in goal.Tasks)
        {
            if (task.Id == candidateTaskId ||
                task.LastDispatch is not { ProviderName: { Length: > 0 } dispatchProviderName } ||
                !dispatchProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) ||
                !IsSubscriptionRetryDeferred(task, now, out var retryAfter))
            {
                continue;
            }

            if (cooldown is null || retryAfter > cooldown.RetryAfter)
            {
                cooldown = new ProviderSubscriptionCooldown(dispatchProviderName, task.Id, retryAfter);
            }
        }

        return cooldown is not null;
    }

    public static bool TryGetSubscriptionLimitRetryAfter(TaskSpec task, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (task.SubscriptionRetryAfter is { } storedRetryAfter)
        {
            retryAfter = storedRetryAfter;
            return true;
        }

        if (task.VerificationHistory.LastOrDefault() is not { Succeeded: false } latest ||
            !IsRecoverableSubscriptionLimitFailure(latest))
        {
            return false;
        }

        // RetryTask is the operator's explicit reset boundary. Keep the historical receipt, but do not
        // let a verification from before that boundary regenerate a deferral after the stored value was cleared.
        if (task.LatestRetryAt is { } latestRetryAt &&
            latest.CompletedAt <= latestRetryAt &&
            task.PendingRetryCause != RetryCause.ProviderInterruption)
        {
            return false;
        }

        return TryGetSubscriptionLimitRetryAfter(latest, out retryAfter);
    }

    public static string DescribeSubscriptionRetrySource(TaskSpec task)
    {
        if (task.SubscriptionRetryAfter is not null)
        {
            return "stored task field SubscriptionRetryAfter";
        }

        if (task.VerificationHistory.LastOrDefault() is { } latest)
        {
            var recordNumber = task.VerificationHistory.Count;
            return $"verification history record {recordNumber} of {recordNumber}, completed {latest.CompletedAt:u}";
        }

        return "retry source unavailable";
    }

    public static bool TryGetSubscriptionLimitRetryAfter(TaskVerificationRecord verification, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (IsDirtyDispatchGuardFailure(verification) ||
            !TryGetRecoverableSubscriptionLimitLine(verification, out var output))
        {
            return false;
        }

        if (TryGetRelativeRetryAfter(output, out var retryAfterDelay))
        {
            retryAfter = verification.CompletedAt.Add(retryAfterDelay);
            return true;
        }

        var marker = "try again at ";
        var markerIndex = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var start = markerIndex + marker.Length;
        var remaining = output[start..].TrimStart();
        var token = string.Join(
            " ",
            remaining
                .Split([' ', '\r', '\n', '\t', '.', ','], StringSplitOptions.RemoveEmptyEntries)
                .Take(2));

        if (!TimeOnly.TryParse(token, out var time))
        {
            return false;
        }

        var basis = verification.CompletedAt;
        retryAfter = new DateTimeOffset(
            basis.Year,
            basis.Month,
            basis.Day,
            time.Hour,
            time.Minute,
            0,
            basis.Offset);

        // Provider notices are emitted before the worker exits. A clock time shortly before completion
        // therefore names an already-expired same-day window, while a substantially earlier time still
        // denotes the next occurrence of that clock time.
        if (retryAfter <= basis && basis - retryAfter > BareClockRetryStalenessTolerance)
        {
            retryAfter = retryAfter.AddDays(1);
        }

        return true;
    }

    private static bool TryGetRecoverableSubscriptionLimitLine(TaskVerificationRecord verification, out string line) =>
        ProviderLimitEvidenceParser.TryGetEvidenceLine(
            EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true),
            out line);

    private static bool TryGetRelativeRetryAfter(string output, out TimeSpan retryAfter)
    {
        var tryAgainMatch = TryAgainInPattern.Match(output);
        if (tryAgainMatch.Success &&
            int.TryParse(tryAgainMatch.Groups["value"].Value, out var retryValue))
        {
            retryAfter = ToTimeSpan(retryValue, tryAgainMatch.Groups["unit"].Value);
            return retryAfter > TimeSpan.Zero;
        }

        var resetsMatch = ResetsInPattern.Match(output);
        if (resetsMatch.Success)
        {
            var hours = ParseIntGroup(resetsMatch, "hours");
            var minutes = ParseIntGroup(resetsMatch, "minutes");
            var seconds = ParseIntGroup(resetsMatch, "seconds");
            retryAfter = new TimeSpan(hours, minutes, seconds);
            return retryAfter > TimeSpan.Zero;
        }

        retryAfter = default;
        return false;
    }

    private static int ParseIntGroup(Match match, string groupName) =>
        match.Groups[groupName].Success && int.TryParse(match.Groups[groupName].Value, out var value)
            ? value
            : 0;

    private static TimeSpan ToTimeSpan(int value, string unit)
    {
        if (unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase) ||
            unit.StartsWith("millisecond", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMilliseconds(value);
        }

        if (unit.StartsWith("m", StringComparison.OrdinalIgnoreCase) &&
            !unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMinutes(value);
        }

        if (unit.StartsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromHours(value);
        }

        return TimeSpan.FromSeconds(value);
    }

    private static bool IsSubscriptionProviderCliDispatch(TaskSpec task)
    {
        var dispatch = task.LastDispatch;
        if (dispatch is null)
        {
            return false;
        }

        if (dispatch.WorkerProviderKind is
            ProviderKind.OpenAICodexCli or
            ProviderKind.AnthropicClaudeCli or
            ProviderKind.OpenAICodexSpark or
            ProviderKind.OpenAICodexOssCli or
            ProviderKind.OllamaQwenCodeCli)
        {
            return true;
        }

        return dispatch.WorkerName.Equals("codex-cli", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("claude-cli", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("codex-spark", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("codex-oss-cli", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("qwen-code-cli", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsRecoverableProviderConnectivityText(string text)
    {
        return (text.Contains("websocket", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("os error", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("10013", StringComparison.OrdinalIgnoreCase))) ||
            text.Contains("Unable to connect to API", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ConnectionRefused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DNS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("host resolution", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ENOTFOUND", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no such host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stream disconnected", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("transport", StringComparison.OrdinalIgnoreCase) &&
             (text.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("failed", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ContainsProviderAuthenticationText(string text)
    {
        return text.Contains("Failed to authenticate", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("API Error 401", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("access token could not be refreshed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("refresh token was already used", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("refresh_token_reused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sign in again", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("log out and sign in", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authorization failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("invalid api key", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsProviderModelRejectionText(string text)
    {
        return (text.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("profile", StringComparison.OrdinalIgnoreCase)) &&
            (text.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("invalid model", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("400", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUsefulPreWorkOutput(string output)
    {
        return HasVerificationEvidence(output, string.Empty) ||
            output.Contains("HUMAN_INPUT:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Model fit:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Changed files:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Files changed:", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetProviderSignalLines(TaskVerificationRecord verification)
    {
        var inWorkerResultBlock = false;
        foreach (var rawLine in EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true))
        {
            var markerLine = rawLine.Trim();
            if (markerLine.Length == 0)
            {
                continue;
            }

            if (IsWorkerResultOpener(markerLine))
            {
                inWorkerResultBlock = true;
                continue;
            }

            if (IsWorkerResultEndMarker(markerLine))
            {
                inWorkerResultBlock = false;
                continue;
            }

            if (inWorkerResultBlock)
            {
                continue;
            }

            if (IsProviderErrorLine(rawLine) ||
                ProviderLimitEvidenceParser.IsProviderLimitEvidenceLine(rawLine))
            {
                yield return rawLine;
            }
        }
    }

    private static bool TryGetProviderAuthenticationLine(TaskVerificationRecord verification, out string line)
    {
        foreach (var candidate in GetProviderSignalLines(verification))
        {
            if (ContainsProviderAuthenticationText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static bool TryGetProviderConnectivityLine(TaskVerificationRecord verification, out string line)
    {
        foreach (var candidate in GetProviderSignalLines(verification))
        {
            if (ContainsRecoverableProviderConnectivityText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static bool TryGetProviderModelRejectionLine(
        TaskVerificationRecord verification,
        out string line) =>
        TryGetProviderModelRejectionLine(
            verification,
            ignoreAuthoritativePlannerContractLines: true,
            out line);

    private static bool TryGetProviderModelRejectionLine(
        TaskVerificationRecord verification,
        bool ignoreAuthoritativePlannerContractLines,
        out string line)
    {
        foreach (var rawLine in EnumerateEvidenceLines(
            verification,
            includeStandardOutput: true,
            includeStandardError: true))
        {
            var candidate = rawLine.Trim();
            if ((!ignoreAuthoritativePlannerContractLines ||
                 !IsPlannerOutputContractFailureLine(candidate)) &&
                ContainsProviderModelRejectionText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static bool IsPlannerOutputContractFailureLine(string line) =>
        line.StartsWith("Planner output contract failed:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Planner durable receipt failed revalidation:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Planner output contract could not persist the accepted plan:", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkerResultOpener(string line)
    {
        var normalized = NormalizeWorkerResultMarker(line).TrimEnd(':').Trim();
        return string.Equals(normalized, "WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkerResultEndMarker(string line)
    {
        var normalized = NormalizeWorkerResultMarker(line);
        return string.Equals(normalized, "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWorkerResultMarker(string text)
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

    private static bool IsProviderErrorLine(string line)
    {
        return line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" : ERROR:", StringComparison.Ordinal) ||
            CodexCliDiagnosticPrefix.IsMatch(line);
    }

    private static bool IsDirtyDispatchGuardFailure(TaskVerificationRecord verification)
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
