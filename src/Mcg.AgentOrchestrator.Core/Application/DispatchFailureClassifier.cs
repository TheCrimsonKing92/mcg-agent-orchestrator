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
    EmptyOutputFlake,
    SandboxCommitBlocked,
    ProviderNeutralProgressStall,
    ProviderAuthentication,
    ProviderConnectivity,
    ProviderModelRejection,
    DirtyWorktreeRecoverable,
    UnknownFailure
}

public sealed record DispatchOutcome(
    DispatchOutcomeKind Kind,
    int ExitCode,
    bool HasZeroByteOutput,
    TimeSpan? RetryAfter,
    TimeSpan? Cooldown,
    RecoveryRecommendation RecoveryRecommendation,
    string EvidenceSummary,
    string ClassifierReceipt = "");

public sealed record ProviderSubscriptionCooldown(
    string ProviderName,
    TaskId SourceTaskId,
    DateTimeOffset RetryAfter);

public static class DispatchFailureClassifier
{
    private enum DispatchRoleOutputCapability
    {
        RequiresChangeEvidence,
        ReadOnly,
        VerificationOnly
    }

    public const int RecoverableSubscriptionLimitReviewThreshold = 2;

    public static DispatchOutcome ClassifyProviderFailure(
        ProviderFailureKind failureKind,
        int exitCode,
        bool hasZeroByteOutput,
        string evidenceSummary)
    {
        return failureKind switch
        {
            ProviderFailureKind.RateLimit => WithClassifierReceipt(
                "provider-rate-limit",
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
                "provider-connectivity",
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
                "provider-sandbox-1312",
                new DispatchOutcome(
                DispatchOutcomeKind.SandboxCommitBlocked,
                exitCode,
                hasZeroByteOutput,
                null,
                null,
                RecoveryRecommendation.CommitAndVerify,
                evidenceSummary),
                exitCode),
            _ => WithClassifierReceipt(
                "provider-unknown",
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

    private static readonly Regex PowerShellNativeErrorPrefix = new(
        "^[^:\\r\\n]{1,120}\\s+:\\s+(?<error>ERROR:|Error:|error:)",
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
    private static readonly Regex StandaloneHttp429Pattern = new(
        @"(?<!\d)429(?!\d)",
        RegexOptions.CultureInvariant);

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

        if (verification.ProviderFailureKind == ProviderFailureKind.RateLimit)
        {
            return true;
        }

        return TryGetRecoverableSubscriptionLimitLine(verification, out _);
    }

    public static bool HasRecoverableSubscriptionLimitEvidence(TaskVerificationRecord verification) =>
        !IsDirtyDispatchGuardFailure(verification) &&
        TryGetRecoverableSubscriptionLimitLine(verification, out _);

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
            return string.IsNullOrWhiteSpace(verification.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardOutput) ||
            !string.IsNullOrWhiteSpace(verification.StandardError))
        {
            return false;
        }

        return true;
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
        providerFailureKind = providerFailureKind == ProviderFailureKind.Unknown
            ? verification.ProviderFailureKind
            : providerFailureKind;
        workerResultPresent = workerResultPresent || verification.WorkerResultPresent;
        hasCommittedChanges = hasCommittedChanges || verification.HasCommittedChanges || HasDispatchResultCommitEvidence(task);
        var exitCode = verification.ExitCode;
        var hasZeroByteOutput = HasZeroByteStandardOutput(verification);

        if (verification.Succeeded &&
            WorkerResultBlockers.TryFindFailingTests(verification, out _))
        {
            return BuildOutcome(
                "succeeded-worker-result-failing-tests",
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
                BuildEvidenceSummary(verification)));
        }

        if (HasGreenCommittedWorkerResultEvidence(verification, workerResultPresent, hasCommittedChanges))
        {
            return BuildOutcome(
                "committed-worker-result-evidence",
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

        if (verification.Succeeded &&
            HasDispatchCompletionEvidence(task, verification, workerResultPresent, hasCommittedChanges))
        {
            return BuildOutcome(
                "succeeded-dispatch-completion-evidence",
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
                "dirty-dispatch-recovery",
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

        if (providerFailureKind != ProviderFailureKind.Unknown &&
            !(providerFailureKind == ProviderFailureKind.RateLimit &&
              HasWorkerEvidenceThatOutranksSubscriptionLimit(verification, workerResultPresent, hasCommittedChanges)))
        {
            var providerOutcome = ClassifyProviderFailure(
                providerFailureKind,
                exitCode,
                hasZeroByteOutput,
                BuildEvidenceSummary(verification));
            return providerOutcome with
            {
                ClassifierReceipt = BuildClassifierReceipt(
                    $"provider-{providerFailureKind}",
                    task,
                    verification,
                    workerResultPresent,
                    hasCommittedChanges,
                    providerOutcome.Kind)
            };
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
                "subscription-limit",
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
                BuildEvidenceSummary(verification)));
        }

        if (IsPreflightFailure(verification))
        {
            return BuildOutcome(
                "preflight-failure",
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

        if (IsRecoverableProviderAuthenticationFailure(verification))
        {
            return BuildOutcome(
                "provider-authentication",
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
                BuildEvidenceSummary(verification)));
        }

        if (IsTransientEmptyOutputDispatchFlake(verification))
        {
            return BuildOutcome(
                "empty-output-flake",
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

        if (IsSandboxCommitBlockedFailure(verification))
        {
            return BuildOutcome(
                "sandbox-commit-blocked",
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
                "provider-neutral-progress-stall",
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

        if (IsRecoverableProviderConnectivityFailure(verification))
        {
            return BuildOutcome(
                "provider-connectivity",
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
                BuildEvidenceSummary(verification)));
        }

        if (IsRecoverableProviderModelRejectionFailure(verification))
        {
            return BuildOutcome(
                "provider-model-rejection",
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

        return BuildOutcome(
            "unknown-failure",
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
            BuildEvidenceSummary(verification)));
    }

    private static DispatchOutcome BuildOutcome(
        string rule,
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges,
        DispatchOutcome outcome) =>
        outcome with
        {
            ClassifierReceipt = BuildClassifierReceipt(rule, task, verification, workerResultPresent, hasCommittedChanges, outcome.Kind)
        };

    private static DispatchOutcome WithClassifierReceipt(string rule, DispatchOutcome outcome, int exitCode) =>
        outcome with
        {
            ClassifierReceipt = $"CLASSIFIER rule={rule}; exit_code={exitCode}; exit_artifact=direct-provider-failure; verdict={outcome.Kind}"
        };

    private static string BuildClassifierReceipt(
        string rule,
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

        return $"CLASSIFIER rule={rule}; exit_code={verification.ExitCode}; exit_artifact=verification-record; " +
            $"stdout_bytes={stdoutBytes}; stderr_bytes={stderrBytes}; heartbeat_stdout_bytes={heartbeat}; " +
            $"worker_result={workerResult}; commit={commitProvenance}; verdict={verdict}";
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
        blockers = string.Empty;
        var combined = $"{verification.StandardOutput}\n{verification.StandardError}";
        var lines = combined.Replace("\r\n", "\n").Split('\n');
        var inBlock = false;
        string? latestBlockers = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (IsWorkerResultOpener(line))
            {
                inBlock = true;
                latestBlockers = null;
                continue;
            }

            if (IsWorkerResultEndMarker(line))
            {
                if (inBlock && latestBlockers is not null)
                {
                    blockers = latestBlockers;
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
            if (string.Equals(key, "blockers", StringComparison.OrdinalIgnoreCase))
            {
                latestBlockers = line[(sep + 1)..].Trim();
            }
        }

        if (inBlock && latestBlockers is not null)
        {
            blockers = latestBlockers;
            return true;
        }

        return false;
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

    private static string SanitizeReceiptValue(string value)
    {
        var sanitized = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace(";", ",", StringComparison.Ordinal)
            .Trim();

        return sanitized.Length > 80 ? sanitized[..80] : sanitized;
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

        if (TryGetRecoverableSubscriptionLimitLine(verification, out var providerLimitLine))
        {
            return TruncateEvidence(providerLimitLine);
        }

        if (TryGetProviderAuthenticationLine(verification, out var providerAuthLine))
        {
            return TruncateEvidence(providerAuthLine);
        }

        if (!HasVerificationEvidence(verification.StandardOutput, verification.StandardError))
        {
            return string.Empty;
        }

        var combined = $"{verification.StandardOutput}\n{verification.StandardError}";
        foreach (var rawLine in combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
        foreach (var line in SplitEvidenceLines(verification.StandardError))
        {
            yield return line;
        }

        foreach (var line in SplitEvidenceLines(ReadEvidenceFile(verification.StandardErrorPath)))
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

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return line;
        }
    }

    private static string ReadEvidenceFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool ContainsPreflightFailureText(string text)
    {
        return text.Contains("Low Integrity", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("integrity label", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("icacls", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CreateProcessAsUser", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("preflight", StringComparison.OrdinalIgnoreCase);
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

        if (workerResultPresent)
        {
            return CanCompleteWithoutChangeEvidence(task, verification) &&
                HasPopulatedStandardOutput(verification);
        }

        return !IsTransientEmptyOutputDispatchFlake(verification);
    }

    private static bool CanCompleteWithoutChangeEvidence(TaskSpec task, TaskVerificationRecord verification) =>
        GetDispatchRoleOutputCapability(task.RequiredRole) switch
        {
            DispatchRoleOutputCapability.ReadOnly => true,
            DispatchRoleOutputCapability.VerificationOnly => !WorkerResultBlockers.TryFindFailingTests(verification, out _),
            _ => false
        };

    private static bool HasPassingVerificationEvidence(TaskVerificationRecord verification) =>
        HasVerificationEvidence(verification.StandardOutput, verification.StandardError) &&
        !WorkerResultBlockers.TryFindFailingTests(verification, out _);

    private static bool HasPopulatedStandardOutput(TaskVerificationRecord verification) =>
        verification.HeartbeatStandardOutputBytes > 0 ||
        !string.IsNullOrWhiteSpace(verification.StandardOutput) ||
        HasStandardOutputFileBytes(verification);

    private static DispatchRoleOutputCapability GetDispatchRoleOutputCapability(AgentRole role) =>
        role switch
        {
            AgentRole.Planner or AgentRole.Researcher or AgentRole.Reviewer => DispatchRoleOutputCapability.ReadOnly,
            AgentRole.Tester => DispatchRoleOutputCapability.VerificationOnly,
            _ => DispatchRoleOutputCapability.RequiresChangeEvidence
        };

    private static bool HasSubstantiveWorkerEvidence(bool workerResultPresent, bool hasCommittedChanges) =>
        workerResultPresent && hasCommittedChanges;

    private static bool HasGreenCommittedWorkerResultEvidence(
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        workerResultPresent &&
        hasCommittedChanges &&
        HasPopulatedStandardOutput(verification) &&
        TryGetWorkerResultBlockersValue(verification, out var blockers) &&
        IsNoWorkerResultBlockersValue(blockers) &&
        !WorkerResultBlockers.TryFindBlocker(verification, out _) &&
        !WorkerResultBlockers.TryFindFailingTests(verification, out _);

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
    public static bool IsSandboxCommitBlockedFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        var output = $"{verification.StandardOutput}\n{verification.StandardError}";
        var commitBlocked =
            output.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("blocked on committing", StringComparison.OrdinalIgnoreCase) ||
            (output.Contains("CreateProcessAsUserW", StringComparison.OrdinalIgnoreCase) &&
             output.Contains("1312", StringComparison.OrdinalIgnoreCase)) ||
            output.Contains("specified logon session does not exist", StringComparison.OrdinalIgnoreCase) ||
            (output.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
             output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));

        // The worker must have reported doing real work — the WORKER_RESULT contract marker, or any
        // other useful-output signal. The downstream auto-verify additionally requires committed
        // changes against main and the acceptance suite, so this is the lightest of several gates.
        var didUsefulWork =
            verification.StandardOutput.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase) ||
            HasUsefulPreWorkOutput(verification.StandardOutput);

        return commitBlocked && didUsefulWork;
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
        return task.Status == WorkTaskStatus.Failed &&
            IsSubscriptionProviderCliDispatch(task) &&
            task.LastVerification is { Succeeded: false } latest &&
            IsRecoverableProviderConnectivityFailure(latest);
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
            IsRecoverableProviderModelRejectionFailure(latest);
    }

    public static int CountRecoverableProviderConnectivityFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableProviderConnectivityFailure(verification));
    }

    public static bool IsRecoverableProviderAuthenticationFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        var output = string.Join(
            Environment.NewLine,
            verification.StandardOutput,
            verification.StandardError);

        return ContainsProviderAuthenticationText(output) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static bool IsRecoverableProviderConnectivityFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        if (verification.ProviderFailureKind == ProviderFailureKind.Connectivity)
        {
            return true;
        }

        var output = string.Join(
            Environment.NewLine,
            verification.StandardOutput,
            verification.StandardError);

        return ContainsRecoverableProviderConnectivityText(output) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static bool IsRecoverableProviderModelRejectionFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        var output = string.Join(
            Environment.NewLine,
            verification.StandardOutput,
            verification.StandardError);

        return ContainsProviderModelRejectionText(output) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static int CountRecoverableProviderModelRejectionFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableProviderModelRejectionFailure(verification));
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
        if (task.VerificationHistory.LastOrDefault() is not { Succeeded: false } latest ||
            !IsRecoverableSubscriptionLimitFailure(latest))
        {
            return false;
        }

        if (task.SubscriptionRetryAfter is { } storedRetryAfter)
        {
            retryAfter = storedRetryAfter;
            return true;
        }

        return TryGetSubscriptionLimitRetryAfter(latest, out retryAfter);
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

        if (retryAfter <= basis)
        {
            retryAfter = retryAfter.AddDays(1);
        }

        return true;
    }

    private static bool TryGetRecoverableSubscriptionLimitLine(TaskVerificationRecord verification, out string line)
    {
        foreach (var candidate in GetProviderSignalLines(verification.StandardOutput, verification.StandardError))
        {
            if (IsRecoverableSubscriptionLimitText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static bool IsRecoverableSubscriptionLimitText(string text)
    {
        return (text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("try again", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("purchase more credits", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("resets in", StringComparison.OrdinalIgnoreCase))) ||
            text.Contains("reached your usage limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rate-limited", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ratelimit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rate limit reached", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("429 Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("http 429", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("status: 429", StringComparison.OrdinalIgnoreCase) ||
            StandaloneHttp429Pattern.IsMatch(text) ||
            text.Contains("retry after", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("try again later due to capacity", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("try again later due to usage", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rate_limit_error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase);
    }

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

        return dispatch.WorkerProviderKind is
            ProviderKind.OpenAICodexCli or
            ProviderKind.AnthropicClaudeCli or
            ProviderKind.OpenAICodexSpark or
            ProviderKind.OpenAICodexOssCli or
            ProviderKind.OllamaQwenCodeCli;
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
            text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
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

    private static IEnumerable<string> GetProviderErrorLines(string output)
    {
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsProviderErrorLine(line))
            {
                yield return line;
                continue;
            }

            var powerShellError = PowerShellNativeErrorPrefix.Match(line);
            if (powerShellError.Success)
            {
                yield return line[powerShellError.Groups["error"].Index..];
            }
        }
    }

    private static IEnumerable<string> GetProviderSignalLines(params string[] outputs)
    {
        foreach (var output in outputs)
        {
            var inWorkerResultBlock = false;
            foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

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

                if (inWorkerResultBlock)
                {
                    continue;
                }

                if (IsProviderErrorLine(line))
                {
                    yield return line;
                    continue;
                }

                var powerShellError = PowerShellNativeErrorPrefix.Match(line);
                if (powerShellError.Success)
                {
                    yield return line[powerShellError.Groups["error"].Index..];
                    continue;
                }

                if (IsBareProviderLimitSignalLine(line))
                {
                    yield return line;
                }
            }
        }
    }

    private static bool TryGetProviderAuthenticationLine(TaskVerificationRecord verification, out string line)
    {
        foreach (var rawLine in $"{verification.StandardOutput}\n{verification.StandardError}".Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = rawLine.Trim();
            if (ContainsProviderAuthenticationText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static bool IsBareProviderLimitSignalLine(string line) =>
        line.StartsWith("You've hit your usage limit", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("reached your usage limit", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("usage limit", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("rate-limited", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("ratelimit", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("exceeded retry limit", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("429 Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("http 429", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("status: 429", StringComparison.OrdinalIgnoreCase) ||
        StandaloneHttp429Pattern.IsMatch(line) ||
        line.Contains("retry after", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("try again later due to capacity", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("try again later due to usage", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("rate_limit_error", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase);

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
        return line.StartsWith("ERROR:", StringComparison.Ordinal) ||
            line.StartsWith("Error:", StringComparison.Ordinal) ||
            line.StartsWith("error:", StringComparison.Ordinal);
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
