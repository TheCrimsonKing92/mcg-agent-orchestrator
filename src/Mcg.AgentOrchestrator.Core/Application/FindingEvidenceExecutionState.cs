namespace Mcg.AgentOrchestrator.Core;

/// <summary>
/// Where a finding's requested focused execution stands for one exact candidate. This is the
/// machine-readable difference between "the current source still has a writable defect" and
/// "execution the conductor itself scheduled has not happened yet". Prose never decides it.
/// </summary>
public enum FindingEvidenceExecutionState
{
    /// <summary>No typed evidence_request exists, so the finding is writable source work now.</summary>
    NoneRequested,

    /// <summary>A typed request exists and no receipt covers this finding at the current candidate.</summary>
    PendingExecution,

    /// <summary>A receipt covering this finding exists at the exact current candidate.</summary>
    ExecutedOnCandidate,

    /// <summary>The request was refused permanently; only a changed request can move it.</summary>
    PermanentlyRefused,

    /// <summary>No validated candidate SHA, so no receipt can be bound to a candidate.</summary>
    CandidateUnknown
}

/// <summary>
/// Deterministic, pure classification of finding evidence execution state. Shared by conductor
/// routing and worker brief building so both describe the same decision from the same rule.
/// </summary>
public static class FindingEvidenceExecutionClassifier
{
    /// <summary>The telemetry placeholder the conductor records when no candidate SHA is validated.</summary>
    public const string UnavailableCandidateSha = "unavailable";

    private const string ExecutedDispositionPrefix = "executed-";
    private const string SupersededDispositionPrefix = "superseded";

    public static int CountEvidenceDeliveryRetries(
        IReadOnlyList<ProgressEvent> timeline,
        TaskId taskId,
        string candidateSha,
        string findingStableId) =>
        timeline.Count(evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.TaskId == taskId &&
            evt.Message.StartsWith("finding evidence-on-demand:", StringComparison.Ordinal) &&
            TryReadMarker(evt.Message, "candidate_sha", out var recordedCandidate) &&
            string.Equals(recordedCandidate, candidateSha, StringComparison.OrdinalIgnoreCase) &&
            TryReadMarker(evt.Message, "finding_ids", out var findingIds) &&
            findingIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(findingStableId, StringComparer.Ordinal));

    public static string ToWireValue(FindingEvidenceExecutionState state) => state switch
    {
        FindingEvidenceExecutionState.NoneRequested => "none-requested",
        FindingEvidenceExecutionState.PendingExecution => "pending-execution",
        FindingEvidenceExecutionState.ExecutedOnCandidate => "executed-on-candidate",
        FindingEvidenceExecutionState.PermanentlyRefused => "permanently-refused",
        FindingEvidenceExecutionState.CandidateUnknown => "candidate-unknown",
        _ => "candidate-unknown"
    };

    /// <summary>
    /// Stable identity of a typed evidence request: its ordered project:class selections.
    /// </summary>
    public static string BuildRequestIdentity(FindingEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = string.Join(
            "|",
            (request.Selections ?? []).Select(selection => $"{selection.TestProject}:{selection.TestClass}"));
        return request.NegativeControl switch
        {
            null => identity,
            FindingEvidenceNegativeControl.RevertSrc => identity + "|negative_control=revert-src",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    /// <summary>
    /// True when the outcome can never be moved by rerunning the same request unchanged. A
    /// receipt-bearing outcome is never a permanent refusal: it already carries executed evidence.
    /// </summary>
    public static bool IsPermanentRefusal(FindingEvidenceOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!string.IsNullOrWhiteSpace(outcome.ReceiptId))
        {
            return false;
        }

        return outcome.Reason switch
        {
            FindingEvidenceNotHonouredReason.Unknown => true, // Permanent default: no typed retry signal exists.
            FindingEvidenceNotHonouredReason.UnsupportedProject => true, // Permanent until the request changes.
            FindingEvidenceNotHonouredReason.UnparseableSelection => true, // Permanent until the request changes.
            FindingEvidenceNotHonouredReason.CandidateShaMissing => false, // Transient: a later candidate may have a sha.
            FindingEvidenceNotHonouredReason.ExecutorUnavailable => false, // Transient: the executor may recover.
            FindingEvidenceNotHonouredReason.SelectionApparatusFailure => false, // Transient: source discovery may recover.
            FindingEvidenceNotHonouredReason.RunFailed => false, // Transient: the focused run may succeed later.
            FindingEvidenceNotHonouredReason.SupersededByActionableRed => true, // Permanent for this unchanged request.
            FindingEvidenceNotHonouredReason.PerRoundCap => true, // Retired persisted disposition; preserve suppression.
            null => true, // Persisted outcomes without a reason are unclassified and fail safe.
            _ => true // Future or unrecognized reasons fail safe against verbatim replay.
        };
    }

    /// <summary>
    /// Classify one finding against the requesting task's own receipts at <paramref name="candidateSha"/>.
    /// Evaluated in order; every branch except <see cref="FindingEvidenceExecutionState.PendingExecution"/>
    /// leaves the finding writable now, so an unknown or missing run can never read as success.
    /// </summary>
    public static FindingEvidenceExecutionState Classify(
        TaskSpec requestingTask,
        ReviewFinding finding,
        string? candidateSha)
    {
        ArgumentNullException.ThrowIfNull(requestingTask);
        ArgumentNullException.ThrowIfNull(finding);

        if (finding.EvidenceRequest is not { } request)
        {
            return FindingEvidenceExecutionState.NoneRequested;
        }

        var normalizedCandidate = candidateSha?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCandidate) ||
            string.Equals(normalizedCandidate, UnavailableCandidateSha, StringComparison.OrdinalIgnoreCase))
        {
            return FindingEvidenceExecutionState.CandidateUnknown;
        }

        if (finding.EvidenceOutcome is { } outcome && IsPermanentRefusal(outcome))
        {
            return FindingEvidenceExecutionState.PermanentlyRefused;
        }

        return HasReceiptOnCandidate(requestingTask, finding, request, normalizedCandidate)
            ? FindingEvidenceExecutionState.ExecutedOnCandidate
            : FindingEvidenceExecutionState.PendingExecution;
    }

    /// <summary>
    /// True only when every supplied finding is still waiting on its scheduled execution for this
    /// exact candidate. One writable finding in the round makes this false, which is what preserves
    /// failure precedence: a demonstrated current defect is never delayed behind a pending run.
    /// </summary>
    public static bool IsPendingExecutionOnly(
        IReadOnlyList<ReviewFinding> findings,
        TaskSpec requestingTask,
        string? candidateSha)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(requestingTask);

        return findings.Count > 0 &&
            findings.All(finding =>
                Classify(requestingTask, finding, candidateSha) == FindingEvidenceExecutionState.PendingExecution);
    }

    private static bool HasReceiptOnCandidate(
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceRequest request,
        string candidateSha)
    {
        var identity = BuildRequestIdentity(request);
        var outcomeReceiptId = finding.EvidenceOutcome?.ReceiptId;
        return requestingTask.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .Where(receipt => string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            .Where(receipt => request.NegativeControl is null ||
                (receipt.Request?.NegativeControl == request.NegativeControl && receipt.NegativeControlOutcome is not null))
            .Any(receipt =>
                CoversFindingOutcome(receipt, outcomeReceiptId) ||
                CoversFindingDisposition(receipt, finding.StableId) ||
                CoversRequestIdentity(receipt, finding.StableId, identity));
    }

    private static bool TryReadMarker(string message, string marker, out string value)
    {
        value = string.Empty;
        var prefix = marker + "=";
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += prefix.Length;
        var end = message.IndexOf(';', start);
        value = message[start..(end < 0 ? message.Length : end)].Trim();
        return value.Length > 0;
    }

    private static bool CoversFindingOutcome(FindingEvidenceReceipt receipt, string? outcomeReceiptId) =>
        !string.IsNullOrWhiteSpace(outcomeReceiptId) &&
        string.Equals(receipt.ReceiptId, outcomeReceiptId, StringComparison.Ordinal);

    // A "pending-*" disposition means the conductor deferred this finding's own request to a later
    // batch, so it is still awaiting execution even though the receipt names it.
    private static bool CoversFindingDisposition(FindingEvidenceReceipt receipt, string stableId) =>
        (receipt.RequestDispositions ?? []).Any(disposition =>
            string.Equals(disposition.FindingStableId, stableId, StringComparison.Ordinal) &&
            IsTerminalDisposition(disposition.Disposition));

    // Legacy receipts predate per-request dispositions; fall back to the executed request identity.
    // A persisted receipt whose non-nullable Request deserialized as null names no request at all, so
    // it covers nothing: this path renders briefs, and a malformed record must not fault that display.
    private static bool CoversRequestIdentity(FindingEvidenceReceipt receipt, string stableId, string identity) =>
        receipt.Request is { } request &&
        (receipt.RequestDispositions ?? []).All(disposition =>
            !string.Equals(disposition.FindingStableId, stableId, StringComparison.Ordinal)) &&
        string.Equals(BuildRequestIdentity(request), identity, StringComparison.Ordinal);

    private static bool IsTerminalDisposition(string disposition) =>
        disposition.StartsWith(ExecutedDispositionPrefix, StringComparison.Ordinal) ||
        disposition.StartsWith(SupersededDispositionPrefix, StringComparison.Ordinal);
}
