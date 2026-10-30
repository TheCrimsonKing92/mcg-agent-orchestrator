namespace Mcg.AgentOrchestrator.Core;

public enum VerificationTerminalContractStatus
{
    NotApplicable,
    Valid,
    Missing,
    Malformed,
    Unreadable,
    Stale,
    Interrupted
}

public sealed record VerificationTerminalContractAssessment(
    VerificationTerminalContractStatus Status, string Diagnostic)
{
    public bool AllowsCompletion => Status is VerificationTerminalContractStatus.NotApplicable or VerificationTerminalContractStatus.Valid;
}

public static partial class WorkerResultBlockers
{
    // This assesses a report, not success: valid blocking and inconclusive reports still
    // belong to their existing outcome owners. No commit or stored success flag is an input.
    public static VerificationTerminalContractAssessment AssessSubscriptionVerification(
        TaskSpec task, TaskVerificationRecord verification) =>
        AssessVerificationAttempt(task, verification, task.LastDispatch);

    public static VerificationTerminalContractAssessment AssessStoredSubscriptionVerification(
        TaskSpec task, TaskVerificationRecord verification) =>
        AssessVerificationAttempt(task, verification, currentDispatch: null);

    private static VerificationTerminalContractAssessment AssessVerificationAttempt(
        TaskSpec task, TaskVerificationRecord verification, TaskDispatchRecord? currentDispatch)
    {
        var dispatch = FindVerificationDispatch(task, verification);
        if (task.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester) ||
            verification.EvidenceSource == VerificationEvidenceSource.Manual)
            return new(VerificationTerminalContractStatus.NotApplicable, string.Empty);
        if (dispatch is null)
            return verification.EvidenceSource == VerificationEvidenceSource.Dispatch
                ? new(VerificationTerminalContractStatus.Stale, "Originating dispatch is unavailable.")
                : new(VerificationTerminalContractStatus.NotApplicable, string.Empty);
        if (!DispatchFailureClassifier.IsSubscriptionProviderCliDispatch(dispatch))
            return new(VerificationTerminalContractStatus.NotApplicable, string.Empty);

        if (verification.DispatchStartedAt is not { } startedAt ||
            startedAt < dispatch.DispatchedAt || verification.CompletedAt < startedAt ||
            (currentDispatch is not null && currentDispatch.DispatchedAt != dispatch.DispatchedAt) ||
            (verification.DispatchAttemptAt is { } attemptAt && attemptAt != dispatch.DispatchedAt))
            return new(VerificationTerminalContractStatus.Stale, "Dispatch attempt identity is missing or mismatched.");

        if (ProviderFailureSignals.IsProviderInterruptionFailure(verification))
            return new(VerificationTerminalContractStatus.Interrupted, "Provider terminal interruption evidence is present.");

        return AssessTerminalVerificationOutput(task.RequiredRole, verification.AuthoritativeStandardOutput);
    }

    private static TaskDispatchRecord? FindVerificationDispatch(TaskSpec task, TaskVerificationRecord verification)
    {
        // Legacy records have no explicit source. Match their recorded command and directory;
        // never impose the worker contract just because a manual/API task has a dispatch history.
        return task.DispatchHistory
            .Where(dispatch => string.Equals(dispatch.Command, verification.Command, StringComparison.Ordinal) &&
                string.Equals(dispatch.WorkingDirectory, verification.WorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
                dispatch.DispatchedAt <= verification.CompletedAt)
            .OrderByDescending(dispatch => dispatch.DispatchedAt)
            .FirstOrDefault();
    }

    public static VerificationTerminalContractAssessment AssessTerminalVerificationOutput(AgentRole role, string? output)
    {
        if (output is null)
            return new(VerificationTerminalContractStatus.Unreadable, "Authoritative current-attempt stdout is unavailable.");

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeWorkerResultLine).ToArray();
        var start = Array.FindLastIndex(lines, IsWorkerResultOpener);
        if (start < 0)
            return new(VerificationTerminalContractStatus.Missing, "Current-attempt stdout has no terminal WORKER_RESULT.");
        var end = Array.FindIndex(lines, start + 1, IsWorkerResultEndMarker);
        if (end < 0)
            return new(VerificationTerminalContractStatus.Malformed, "Final WORKER_RESULT is missing END_WORKER_RESULT.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = start + 1; index < end; index++)
        {
            if (TryFindField(lines[index], out var key, out var value) && !fields.TryAdd(key, value))
                return Malformed($"Duplicate terminal field: {key}.");
        }
        foreach (var required in AgentOutputDirectives.RequiredWorkerResultFieldNamesForRole(role))
        {
            if (!fields.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value) ||
                (value.StartsWith('<') && value.EndsWith('>')))
                return Malformed($"Missing or unpopulated terminal field: {required}.");
        }
        if (TryFindMalformedEvidenceBoundOutcome(output, out var outcomeDiagnostic))
            return Malformed(outcomeDiagnostic);
        if (!TryParseTestsStatus(fields["tests"], out var testsStatus))
            return Malformed($"Terminal field tests must start with pass, fail, deferred, not-run or 'inconclusive - <evidence>'; got {QuoteTerminalFieldValue(fields["tests"])}.");
        if (!TryParseBlockersStatus(fields["blockers"], out _))
            return Malformed($"Terminal field blockers must start with none or a blocker description; got {QuoteTerminalFieldValue(fields["blockers"])}.");

        // Parse only the captured final block: no mutable artifact re-read or stderr fallback.
        var capturedBlock = string.Join('\n', lines[start..(end + 1)]);
        var terminal = new TaskVerificationRecord("contract", "", 0,
            capturedBlock, "", DateTimeOffset.MinValue)
        {
            // Transient parsing must not discard decision evidence through the preview retention bound.
            StandardOutput = capturedBlock
        };
        if (role == AgentRole.Reviewer)
        {
            var verdict = fields["verdict"].ToLowerInvariant();
            if (verdict is not ("pass" or "needs-work" or "fail" or "blocked-at-cap"))
                return Malformed($"Reviewer field verdict must be pass, needs-work, fail or blocked-at-cap; got {QuoteTerminalFieldValue(fields["verdict"])}.");
            if (!TryFindReviewFindingRound(terminal, out _, out var findingsDiagnostic))
                return Malformed("Reviewer field findings is malformed: " + findingsDiagnostic);
            if (!TryFindCriteriaVerdicts(terminal, out _, out var criteriaDiagnostic))
                return Malformed("Reviewer field criteria_verdicts is malformed: " + criteriaDiagnostic);
            // Parsed findings and criterion verdicts are reports, not completion authority.
            // The kernel applies convergence, operator waivers and authoritative ownership
            // before deciding whether a complete report can pass its semantic gates.
        }
        if (role == AgentRole.Tester && testsStatus == TestsStatus.Pass)
        {
            // The existing status parser accepts punctuation after the leading pass token.
            // Require evidence without narrowing those established presentation forms.
            var passEvidence = fields["tests"].Trim()["pass".Length..].TrimStart(' ', '-', '—', '–', ':').Trim();
            if (passEvidence.Length == 0 || IsNoBlockerValue(passEvidence) ||
                (passEvidence.StartsWith('<') && passEvidence.EndsWith('>')))
                return Malformed("Tester pass requires a current-round evidence reference.");
        }

        return new(VerificationTerminalContractStatus.Valid, string.Empty);
    }

    private static string QuoteTerminalFieldValue(string value)
    {
        var trimmed = value.Trim();
        return "'" + (trimmed.Length > 160 ? trimmed[..160] + "..." : trimmed) + "'";
    }

    private static VerificationTerminalContractAssessment Malformed(string diagnostic) =>
        new(VerificationTerminalContractStatus.Malformed, diagnostic);
}
