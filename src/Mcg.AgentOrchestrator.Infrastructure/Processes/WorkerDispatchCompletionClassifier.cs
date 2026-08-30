using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerDispatchCompletionClassifier
{
    private readonly Func<TaskDispatchRecord, IWorkerProvider> _resolveWorkerProvider;
    private readonly IClock _clock;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string> _readArtifactText;

    internal WorkerDispatchCompletionClassifier(
        Func<TaskDispatchRecord, IWorkerProvider> resolveWorkerProvider,
        IClock clock,
        Func<string, bool> fileExists,
        Func<string, string> readArtifactText)
    {
        _resolveWorkerProvider = resolveWorkerProvider;
        _clock = clock;
        _fileExists = fileExists;
        _readArtifactText = readArtifactText;
    }

    internal bool RequiresPostDispatchCommitEvidence(
        TaskSpec task,
        bool hasVerificationOnlyTesterCompletion)
    {
        return task.RequiredRole switch
        {
            AgentRole.Developer => true,
            AgentRole.Tester => !hasVerificationOnlyTesterCompletion,
            _ => false
        };
    }

    internal bool IsVerificationOnlyTesterCompletion(
        TaskSpec task,
        string standardOutput,
        bool hasCompletedVerification)
    {
        return task.RequiredRole == AgentRole.Tester &&
            (hasCompletedVerification ||
             DispatchFailureClassifier.HasWorkerResultDeferralInOutput(standardOutput));
    }

    // A verification-role worker proves it did its job with recognised verification evidence.
    // WORKER_RESULT shape alone is not enough: evidence-less clean dispatches must fail so the
    // orchestrator does not convert a well-formed self-report into proof that checks actually passed.
    internal bool HasClassifiedVerificationEvidence(string standardOutput, string standardError)
    {
        return DispatchFailureClassifier.HasVerificationEvidenceInOutput(standardOutput, standardError);
    }

    internal bool HasCompletedVerification(string standardOutput, string standardError)
    {
        return HasClassifiedVerificationEvidence(standardOutput, standardError) &&
            !TryFindFailingTestsInWorkerResult(standardOutput, standardError, out _);
    }

    internal bool HasReportedFailingVerification(string standardOutput, string standardError)
    {
        return TryFindFailingTestsInWorkerResult(standardOutput, standardError, out _);
    }

    internal bool HasSandboxCommitBlockedEvidence(
        AgentRole role,
        string standardOutput,
        string standardError)
    {
        return DispatchFailureClassifier.IsSandboxCommitBlockedFailure(
            role,
            1,
            standardOutput,
            standardError);
    }

    internal bool HasLowIntegrityConfinementEvidence(
        TaskDispatchRecord? dispatch,
        TaskProcessRecord processRecord,
        string standardError,
        bool sandboxCommitOnBehalfEvidence)
    {
        if (dispatch?.SandboxLowIntegrity != true)
        {
            return false;
        }

        return sandboxCommitOnBehalfEvidence ||
            HasCompletedSandboxPreparationEvent(standardError) ||
            HasLowIntegritySetupArtifact(processRecord.WorkingDirectory);
    }

    internal bool HasCompletedSandboxPreparationEvent(string standardError)
    {
        foreach (var line in standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("sandbox-prep", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("event", out var evt) &&
                    root.TryGetProperty("phase", out var phase) &&
                    string.Equals(evt.GetString(), "sandbox-prep", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(phase.GetString(), "complete", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    internal bool HasLowIntegritySetupArtifact(string workingDirectory)
    {
        try
        {
            return _fileExists(Path.Combine(workingDirectory, ".mcg-sandbox", DispatchProcessHost.LowIntegritySetupArtifactName));
        }
        catch
        {
            return false;
        }
    }

    internal ProviderFailureKind ParseProviderFailureKind(
        TaskDispatchRecord? dispatch,
        int exitCode,
        string standardOutput,
        string standardError)
    {
        if (dispatch is null)
        {
            return ProviderFailureKind.Unknown;
        }

        return _resolveWorkerProvider(dispatch).ParseOutcome(new WorkerProviderOutcome(
            exitCode,
            standardOutput,
            standardError));
    }

    internal bool HasWorkerResultArtifact(string workingDirectory, string standardOutput)
    {
        if (WorkerResultParser.TryParseFields(standardOutput, out _, out _))
        {
            return true;
        }

        foreach (var fileName in new[] { "WORKER_RESULT.md", "WORKER_RESULT.txt" })
        {
            var path = Path.Combine(workingDirectory, fileName);
            if (!_fileExists(path))
            {
                continue;
            }

            try
            {
                if (WorkerResultParser.TryParseFields(_readArtifactText(path), out _, out _))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    internal bool HasSuccessfulWorkerResult(
        string workingDirectory,
        string standardOutput,
        string standardError,
        bool allowNoChangedFiles = false,
        bool requireNoBlockers = false,
        DispatchRoleOutputCapability? roleCapability = null)
    {
        if (WorkerResultParser.TryParseSuccessfulResult(
                $"{standardOutput}\n{standardError}",
                out _,
                out _,
                allowNoChangedFiles,
                requireNoBlockers,
                allowReadOnlyTestStatuses: roleCapability == DispatchRoleOutputCapability.ReadOnly))
        {
            return true;
        }

        foreach (var fileName in new[] { "WORKER_RESULT.md", "WORKER_RESULT.txt" })
        {
            var path = Path.Combine(workingDirectory, fileName);
            if (!_fileExists(path))
            {
                continue;
            }

            try
            {
                if (WorkerResultParser.TryParseSuccessfulResult(
                        _readArtifactText(path),
                        out _,
                        out _,
                        allowNoChangedFiles,
                        requireNoBlockers,
                        allowReadOnlyTestStatuses: roleCapability == DispatchRoleOutputCapability.ReadOnly))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    internal bool TryFindFailedWorkerBuildCheck(
        string workingDirectory,
        string standardOutput,
        string standardError,
        out string diagnostic)
    {
        if (TryFindFailedWorkerBuildCheckInText($"{standardOutput}\n{standardError}", out diagnostic))
        {
            return true;
        }

        foreach (var fileName in new[] { "WORKER_RESULT.md", "WORKER_RESULT.txt" })
        {
            var path = Path.Combine(workingDirectory, fileName);
            if (!_fileExists(path))
            {
                continue;
            }

            try
            {
                if (TryFindFailedWorkerBuildCheckInText(_readArtifactText(path), out diagnostic))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        diagnostic = string.Empty;
        return false;
    }

    internal bool TryFindFailedWorkerBuildCheckInText(string text, out string diagnostic)
    {
        if (!WorkerResultParser.TryParseResult(text, out var result, out _) ||
            !WorkerResultParser.WorkerBuildCheckTestsReportFailure(result, out var tests))
        {
            diagnostic = string.Empty;
            return false;
        }

        diagnostic = $"WORKER_RESULT reported failed worker build check: {tests}";
        return true;
    }

    internal bool HasWorkerBuildEvidence(
        string workingDirectory,
        string standardOutput,
        string standardError)
    {
        if (WorkerResultParser.TryParseResult(standardOutput, out var result, out _) ||
            WorkerResultParser.TryParseResult(standardError, out result, out _))
        {
            return WorkerResultParser.WorkerBuildCheckTestsReportSuccess(result);
        }

        foreach (var fileName in new[] { "WORKER_RESULT.md", "WORKER_RESULT.txt" })
        {
            var path = Path.Combine(workingDirectory, fileName);
            if (!_fileExists(path))
            {
                continue;
            }

            try
            {
                if (HasWorkerBuildEvidenceInText(_readArtifactText(path)))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    internal static bool HasWorkerBuildEvidenceInText(string text) =>
        WorkerResultParser.TryParseResult(text, out var result, out _) &&
        WorkerResultParser.WorkerBuildCheckTestsReportSuccess(result);

    internal bool TryFindFailingTestsInWorkerResult(
        string standardOutput,
        string standardError,
        out string tests)
    {
        // stdout and stderr are independently ordered streams. Concatenating them lets a retained
        // prompt/schema block in noisy stderr supersede the worker's real final stdout result.
        // Prefer a complete stdout result and consult stderr only when stdout has none.
        if (WorkerResultParser.TryParseResult(standardOutput, out var result, out _) ||
            WorkerResultParser.TryParseResult(standardError, out result, out _))
        {
            return WorkerResultParser.TestsReportFailure(result, out tests);
        }

        tests = string.Empty;
        return false;
    }

    internal bool HasExplicitNoChangeRationale(string standardOutput, string standardError)
    {
        var output = $"{standardOutput}\n{standardError}";
        return output.Contains("NO_CHANGE:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("No-change rationale:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("No changes needed:", StringComparison.OrdinalIgnoreCase);
    }

    internal bool AllowsNoChangeCompletion(TaskSpec task, string standardOutput, string standardError)
    {
        if (task.RequiredRole != AgentRole.Developer)
        {
            return HasExplicitNoChangeRationale(standardOutput, standardError);
        }

        var receipt = task.LastDispatch?.ContextPackageReceipt;
        if (!HasExplicitNoChangeRationale(standardOutput, standardError) ||
            receipt is not { EarlyConvergenceEligible: true } ||
            string.IsNullOrWhiteSpace(receipt.EarlyConvergenceCandidateSha) ||
            !string.Equals(receipt.EarlyConvergenceCandidateSha, task.LastDispatch?.BaseCommit, StringComparison.OrdinalIgnoreCase) ||
            receipt.EarlyConvergenceReceiptHashes is not { Count: > 0 })
        {
            return false;
        }

        return (WorkerResultParser.TryParseResult(standardOutput, out var result, out _) ||
                WorkerResultParser.TryParseResult(standardError, out result, out _)) &&
            result.BlockersStatus == WorkerResultParser.BlockersStatus.None &&
            result.TestsStatus == WorkerResultParser.TestsStatus.Pass;
    }

    internal string? ClassifyReconciliationOriginRule(
        TaskSpec task,
        TaskProcessRecord processRecord,
        int observedRootExitCode,
        string standardOutput,
        string standardError,
        string? standardErrorDiagnostic,
        bool workerResultPresent,
        bool hasCommittedChanges,
        ProviderFailureKind providerFailureKind,
        DispatchProcessHost.DispatchChildExitRecord childExitRecord,
        DispatchRecoveryDecision? recoveryDecision)
    {
        var diagnosticStandardError = AppendDiagnostic(standardError, standardErrorDiagnostic ?? string.Empty);
        var recordedOriginRule = TaskOutcomeClassifier.TryExtractRule(standardOutput) ??
            TaskOutcomeClassifier.TryExtractRule(diagnosticStandardError);
        if (!string.IsNullOrWhiteSpace(recordedOriginRule))
        {
            return recordedOriginRule;
        }

        // PreserveInterruptedWork is the typed dirty-dispatch boundary that the failure classifier
        // reports as dirty-dispatch-recovery after a failed verification is recorded. Capture that
        // diagnostic origin before successful reconciliation changes the logical task disposition.
        // This metadata never participates in ShouldReconcileWrapperExit.
        if (recoveryDecision?.Action == DispatchRecoveryAction.PreserveInterruptedWork)
        {
            return "dirty-dispatch-recovery";
        }

        var verification = new TaskVerificationRecord(
            processRecord.Command,
            processRecord.WorkingDirectory,
            observedRootExitCode,
            standardOutput,
            diagnosticStandardError,
            _clock.UtcNow,
            StandardOutputPath: processRecord.StandardOutputPath,
            StandardErrorPath: processRecord.StandardErrorPath,
            WorkerResultPresent: workerResultPresent,
            HasCommittedChanges: hasCommittedChanges,
            ProviderFailureKind: providerFailureKind,
            DispatchStartedAt: processRecord.StartedAt,
            ChildProcessId: childExitRecord.ProcessId,
            ChildExitCode: childExitRecord.ExitCode,
            ObservedRootExitCode: observedRootExitCode,
            FullStandardOutput: standardOutput,
            FullStandardError: diagnosticStandardError);
        var origin = DispatchFailureClassifier.Classify(
            task,
            verification,
            providerFailureKind,
            workerResultPresent,
            hasCommittedChanges);
        return TaskOutcomeClassifier.TryExtractRule(origin.ClassifierReceipt);
    }

    internal static bool ShouldReconcileWrapperExit(BackgroundDispatchRunner.WrapperExitReconciliationEvidence evidence) =>
        evidence.ObservedRootExitCode != 0 &&
        evidence.ChildExitCode == 0 &&
        evidence.HasCompleteNonBlockedWorkerResult &&
        evidence.CompletionContractSucceeded &&
        evidence.HasKnownRoleCapability &&
        (evidence.RoleCapability != DispatchRoleOutputCapability.RequiresChangeEvidence ||
         evidence.HasRelevantChangeEvidence) &&
        !evidence.HasTerminalHumanInputDirective &&
        !evidence.HasFatalOrchestratorFailure;

    private static string AppendDiagnostic(string standardError, string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return standardError;
        }

        return string.IsNullOrEmpty(standardError)
            ? diagnostic
            : standardError.TrimEnd() + Environment.NewLine + diagnostic;
    }
}
