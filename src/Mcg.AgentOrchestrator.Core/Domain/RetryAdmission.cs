using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public enum RetryCause
{
    Unknown,
    NewSourceFinding,
    NewTestFinding,
    CriterionEvidenceOwnerMismatch,
    EnvironmentApparatusFailure,
    ContractClarification,
    MainDriftConflict,
    ProviderInterruption,
    UnchangedContextRepeat
}

public enum PaidRouteClassification
{
    Unknown,
    NonPaid,
    Paid
}

public enum RetryAdmissionDecision
{
    Allowed,
    Prevented,
    ResumedReservation
}

public enum RetryAdmissionRoute
{
    SameRole,
    EvidenceLane,
    UpstreamImplementation,
    HumanClarification,
    AcceptanceRegate,
    EnvironmentalHold,
    ReservationLease
}

public sealed record RetryContextFingerprint(int SchemaVersion, string Value)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record RetryContextFingerprintInput(
    string GoalId,
    string TaskId,
    AgentRole Role,
    string? ProviderName,
    string? ModelName,
    PaidRouteClassification PaidRoute,
    string? ReviewedCandidateSha,
    string? EffectiveCriteriaHash,
    IReadOnlyList<string> OpenFindingIdentities,
    IReadOnlyList<string> EvidenceReceiptIdentities,
    IReadOnlyList<string> RetryFeedback,
    IReadOnlyList<string> AuthoritativeDecisions,
    string? BaseIdentity,
    string? MainIdentity);

public sealed record RetryAdmissionReceipt(
    string ReceiptId,
    RetryCause Cause,
    RetryContextFingerprint Fingerprint,
    RetryAdmissionDecision Decision,
    RetryAdmissionRoute Route,
    PaidRouteClassification PaidRoute,
    DateTimeOffset LinkedDispatchAt,
    DateTimeOffset RecordedAt,
    DateTimeOffset? PriorAttemptAt = null,
    DateTimeOffset? WorkerStartedAt = null,
    string? ReservationOwnerId = null,
    DateTimeOffset? ReservationLeaseExpiresAt = null);

public sealed record RetryAdmissionResult(
    RetryAdmissionDecision Decision,
    RetryAdmissionReceipt Receipt)
{
    public bool AllowsProcessStart => Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation;
}

public sealed record RetryAdmissionSnapshotResult(
    GoalSnapshot Snapshot,
    RetryAdmissionResult Admission);

public sealed record RetryAdmissionStartClaimResult(
    GoalSnapshot Snapshot,
    bool Claimed);

public static class RetryAdmissionSnapshotReservation
{
    public static RetryAdmissionSnapshotResult Apply(
        GoalSnapshot snapshot,
        TaskId taskId,
        RetryContextFingerprint fingerprint,
        PaidRouteClassification paidRoute,
        RetryCause cause,
        TaskDispatchRecord preparedDispatch,
        DateTimeOffset recordedAt,
        string reservationOwnerId,
        DateTimeOffset reservationLeaseExpiresAt,
        bool reservationRecoveryConfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var goal = Goal.FromSnapshot(snapshot);
        var task = goal.FindTask(taskId);
        var result = RetryAdmissionPolicy.Evaluate(
            task,
            fingerprint,
            paidRoute,
            cause,
            preparedDispatch.DispatchedAt,
            recordedAt,
            RetryContextFingerprintFactory.GetOpenBlockingFindings(goal),
            reservationOwnerId,
            reservationLeaseExpiresAt,
            reservationRecoveryConfirmed);
        if (result.AllowsProcessStart)
            task.BindPreparedDispatch(preparedDispatch);
        task.RecordRetryAdmission(result.Receipt);
        if (!result.AllowsProcessStart)
            task.SetRetryAdmissionHold(result.Receipt.Route);
        return new RetryAdmissionSnapshotResult(goal.ToSnapshot(), result);
    }
}

public static class RetryAdmissionSnapshotStartClaim
{
    public static RetryAdmissionStartClaimResult Apply(
        GoalSnapshot snapshot,
        TaskId taskId,
        DateTimeOffset linkedDispatchAt,
        string reservationOwnerId,
        DateTimeOffset workerStartedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var goal = Goal.FromSnapshot(snapshot);
        var claimed = goal.FindTask(taskId).TryClaimRetryAdmissionStart(
            linkedDispatchAt,
            reservationOwnerId,
            workerStartedAt);
        return new RetryAdmissionStartClaimResult(goal.ToSnapshot(), claimed);
    }
}

public static class RetryContextFingerprintBuilder
{
    private const string Domain = "mcg-retry-context-fingerprint";

    public static RetryContextFingerprint Build(RetryContextFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var stream = new MemoryStream();
        Append(stream, Domain);
        Append(stream, RetryContextFingerprint.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(stream, input.GoalId);
        Append(stream, input.TaskId);
        Append(stream, input.Role.ToString());
        Append(stream, input.ProviderName);
        Append(stream, input.ModelName);
        Append(stream, input.PaidRoute.ToString());
        Append(stream, input.ReviewedCandidateSha);
        Append(stream, input.EffectiveCriteriaHash);
        AppendCollection(stream, input.OpenFindingIdentities);
        AppendCollection(stream, input.EvidenceReceiptIdentities);
        AppendCollection(stream, input.RetryFeedback.Select(NormalizeLineEndings).ToArray());
        AppendCollection(stream, input.AuthoritativeDecisions);
        Append(stream, input.BaseIdentity);
        Append(stream, input.MainIdentity);
        return new RetryContextFingerprint(
            RetryContextFingerprint.CurrentSchemaVersion,
            Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant());
    }

    private static void AppendCollection(Stream stream, IReadOnlyList<string> values)
    {
        var canonical = values
            .Select(value => value ?? string.Empty)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Append(stream, canonical.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var value in canonical)
            Append(stream, value);
    }

    private static void Append(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }

        stream.WriteByte(1);
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
}

public static class RetryContextFingerprintFactory
{
    public static RetryContextFingerprint Build(
        Goal goal,
        TaskSpec task,
        string? providerName,
        string? modelName,
        PaidRouteClassification paidRoute,
        string? reviewedCandidateSha,
        string? baseIdentity,
        string? mainIdentity)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(task);
        var latestFindingSet = goal.Tasks
            .SelectMany(candidate => candidate.VerificationHistory)
            .Where(verification => verification.MergedReviewFindings is { Count: > 0 })
            .OrderBy(verification => verification.CompletedAt)
            .Select(verification => verification.MergedReviewFindings!)
            .LastOrDefault() ?? [];
        var openFindings = ReviewFindings.GetEffectiveOpenFindings(
            latestFindingSet,
            goal.EffectiveAcceptanceCriteriaCorrections);
        var findingIdentities = openFindings.Select(finding => string.Join(
            "\u001f",
            finding.StableId,
            finding.Category,
            finding.Severity,
            finding.Location.File,
            finding.Location.Region,
            finding.Location.Hunk ?? "<null>")).ToArray();
        var evidenceIdentities = goal.Tasks
            .SelectMany(candidate => candidate.VerificationHistory)
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .Select(receipt => string.Join(
                "\u001f",
                receipt.ReceiptId,
                receipt.CandidateSha,
                receipt.Accepted,
                receipt.Passed,
                receipt.FindingRoundFingerprint ?? "<null>"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var authoritativeDecisions = goal.RefinedSpec is null
            ? []
            : goal.RefinedSpec.Decisions
                .Select(decision => $"decision\u001f{decision.Question}\u001f{decision.Choice}\u001f{decision.Rationale}")
                .Concat(goal.RefinedSpec.AuthoritativeClarificationAnswerHistory.Select(answer =>
                    $"answer\u001f{answer.Id}\u001f{answer.Text}\u001f{answer.Origin}\u001f{answer.BriefVersion?.ToString() ?? "<null>"}"))
                .ToArray();
        var criteriaHash = goal.RefinedSpec is null
            ? null
            : EffectiveAcceptanceCriteriaVersion.ComputeHash(
                goal.RefinedSpec,
                goal.EffectiveAcceptanceCriteriaCorrections);
        var latestOperatorRetryFeedback = goal.Timeline
            .Where(item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskRetried)
            .OrderBy(item => item.OccurredAt)
            .LastOrDefault()?.Message;
        var retryFeedback = string.IsNullOrWhiteSpace(latestOperatorRetryFeedback)
            ? task.CriterionRetryFeedback
            : task.CriterionRetryFeedback.Concat([latestOperatorRetryFeedback]).ToArray();
        return RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            goal.Id.Value,
            task.Id.Value,
            task.RequiredRole,
            providerName,
            modelName,
            paidRoute,
            reviewedCandidateSha,
            criteriaHash,
            findingIdentities,
            evidenceIdentities,
            retryFeedback,
            authoritativeDecisions,
            baseIdentity,
            mainIdentity));
    }

    public static IReadOnlyList<ReviewFinding> GetOpenBlockingFindings(Goal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var latestFindingSet = goal.Tasks
            .SelectMany(task => task.VerificationHistory)
            .Where(verification => verification.MergedReviewFindings is { Count: > 0 })
            .OrderBy(verification => verification.CompletedAt)
            .Select(verification => verification.MergedReviewFindings!)
            .LastOrDefault() ?? [];
        return ReviewFindings.GetOpenBlockingFindings(
            latestFindingSet,
            goal.EffectiveAcceptanceCriteriaCorrections);
    }
}

public static class RetryAdmissionPolicy
{
    public static RetryAdmissionResult Evaluate(
        TaskSpec task,
        RetryContextFingerprint fingerprint,
        PaidRouteClassification paidRoute,
        RetryCause cause,
        DateTimeOffset linkedDispatchAt,
        DateTimeOffset recordedAt,
        IReadOnlyList<ReviewFinding>? openBlockingFindings = null,
        string? reservationOwnerId = null,
        DateTimeOffset? reservationLeaseExpiresAt = null,
        bool reservationRecoveryConfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(fingerprint);
        var isPaidRetry = paidRoute == PaidRouteClassification.Paid && task.LatestRetryAt is not null;
        var sameAttempt = task.RetryAdmissionHistory.LastOrDefault(receipt =>
            receipt.LinkedDispatchAt == linkedDispatchAt &&
            receipt.Fingerprint == fingerprint &&
            receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation);
        if (sameAttempt is not null && sameAttempt.WorkerStartedAt is null)
        {
            if (!string.IsNullOrWhiteSpace(reservationOwnerId) &&
                string.Equals(sameAttempt.ReservationOwnerId, reservationOwnerId, StringComparison.Ordinal))
            {
                return new RetryAdmissionResult(sameAttempt.Decision, sameAttempt);
            }

            if (!string.IsNullOrWhiteSpace(reservationOwnerId) &&
                reservationRecoveryConfirmed &&
                (string.IsNullOrWhiteSpace(sameAttempt.ReservationOwnerId) ||
                 sameAttempt.ReservationLeaseExpiresAt is { } leaseExpiresAt && recordedAt >= leaseExpiresAt))
            {
                return Create(
                    task,
                    fingerprint,
                    paidRoute,
                    cause,
                    RetryAdmissionDecision.ResumedReservation,
                    sameAttempt.Route,
                    linkedDispatchAt,
                    recordedAt,
                    sameAttempt.PriorAttemptAt,
                    reservationOwnerId,
                    reservationLeaseExpiresAt);
            }

            return Create(
                task,
                fingerprint,
                paidRoute,
                cause,
                RetryAdmissionDecision.Prevented,
                RetryAdmissionRoute.ReservationLease,
                linkedDispatchAt,
                recordedAt,
                sameAttempt.LinkedDispatchAt,
                reservationOwnerId,
                reservationLeaseExpiresAt);
        }

        if (sameAttempt?.WorkerStartedAt is not null)
        {
            return Create(
                task,
                fingerprint,
                paidRoute,
                cause,
                RetryAdmissionDecision.Prevented,
                RetryAdmissionRoute.ReservationLease,
                linkedDispatchAt,
                recordedAt,
                sameAttempt.LinkedDispatchAt,
                reservationOwnerId,
                reservationLeaseExpiresAt);
        }

        var priorSameContext = isPaidRetry
            ? task.RetryAdmissionHistory.LastOrDefault(receipt =>
                receipt.PaidRoute == PaidRouteClassification.Paid &&
                receipt.LinkedDispatchAt != linkedDispatchAt &&
                receipt.Fingerprint == fingerprint &&
                receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation)
            : null;
        if (priorSameContext is not null)
        {
            return Create(
                task,
                fingerprint,
                paidRoute,
                RetryCause.UnchangedContextRepeat,
                RetryAdmissionDecision.Prevented,
                ResolveRoute(cause, openBlockingFindings ?? []),
                linkedDispatchAt,
                recordedAt,
                priorSameContext.LinkedDispatchAt,
                reservationOwnerId,
                reservationLeaseExpiresAt);
        }

        var priorAttemptAt = isPaidRetry
            ? task.LastDispatch?.DispatchedAt == linkedDispatchAt
                ? task.DispatchHistory.SkipLast(1).LastOrDefault()?.DispatchedAt
                : task.DispatchHistory.LastOrDefault()?.DispatchedAt
            : null;
        return Create(
            task,
            fingerprint,
            paidRoute,
            cause,
            RetryAdmissionDecision.Allowed,
            RetryAdmissionRoute.SameRole,
            linkedDispatchAt,
            recordedAt,
            priorAttemptAt,
            reservationOwnerId,
            reservationLeaseExpiresAt);
    }

    private static RetryAdmissionResult Create(
        TaskSpec task,
        RetryContextFingerprint fingerprint,
        PaidRouteClassification paidRoute,
        RetryCause cause,
        RetryAdmissionDecision decision,
        RetryAdmissionRoute route,
        DateTimeOffset linkedDispatchAt,
        DateTimeOffset recordedAt,
        DateTimeOffset? priorAttemptAt,
        string? reservationOwnerId,
        DateTimeOffset? reservationLeaseExpiresAt)
    {
        var identity = string.Join(
            "\n",
            task.Id.Value,
            fingerprint.Value,
            linkedDispatchAt.ToUniversalTime().Ticks,
            decision,
            reservationOwnerId ?? string.Empty);
        var receipt = new RetryAdmissionReceipt(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant(),
            cause,
            fingerprint,
            decision,
            route,
            paidRoute,
            linkedDispatchAt,
            recordedAt,
            priorAttemptAt,
            ReservationOwnerId: reservationOwnerId,
            ReservationLeaseExpiresAt: reservationLeaseExpiresAt);
        return new RetryAdmissionResult(decision, receipt);
    }

    private static RetryAdmissionRoute ResolveRoute(
        RetryCause cause,
        IReadOnlyList<ReviewFinding> openBlockingFindings)
    {
        if (cause is RetryCause.ProviderInterruption or RetryCause.EnvironmentApparatusFailure)
            return RetryAdmissionRoute.EnvironmentalHold;
        if (cause is RetryCause.ContractClarification or RetryCause.Unknown)
            return RetryAdmissionRoute.HumanClarification;
        if (cause == RetryCause.CriterionEvidenceOwnerMismatch)
            return RetryAdmissionRoute.EvidenceLane;
        if (cause == RetryCause.NewTestFinding)
            return RetryAdmissionRoute.EvidenceLane;
        if (cause is RetryCause.NewSourceFinding or RetryCause.MainDriftConflict)
            return RetryAdmissionRoute.UpstreamImplementation;

        if (openBlockingFindings.Count == 0)
            return RetryAdmissionRoute.AcceptanceRegate;
        var route = ReviewFindingRouting.Resolve(openBlockingFindings, string.Empty);
        if (route.EscalateToOperator)
            return RetryAdmissionRoute.HumanClarification;
        return route.TargetRole == AgentRole.Tester
            ? RetryAdmissionRoute.EvidenceLane
            : RetryAdmissionRoute.UpstreamImplementation;
    }
}
