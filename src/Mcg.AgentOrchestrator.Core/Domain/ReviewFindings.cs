using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ReviewFindingState>))]
public enum ReviewFindingState
{
    Open,
    Resolved
}

[JsonConverter(typeof(FindingSeverityJsonConverter))]
public enum FindingSeverity
{
    Blocking = 0,
    Advisory = 1
}

public sealed class FindingSeverityJsonConverter : JsonConverter<FindingSeverity>
{
    public override bool HandleNull => true;

    public override FindingSeverity Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return Enum.TryParse<FindingSeverity>(value, ignoreCase: true, out var severity) &&
                   Enum.IsDefined(severity)
                ? severity
                : FindingSeverity.Blocking;
        }

        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt32(out var numericSeverity) &&
            Enum.IsDefined(typeof(FindingSeverity), numericSeverity))
        {
            return (FindingSeverity)numericSeverity;
        }

        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
        {
            using var ignored = JsonDocument.ParseValue(ref reader);
        }

        return FindingSeverity.Blocking;
    }

    public override void Write(
        Utf8JsonWriter writer,
        FindingSeverity value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(
            value == FindingSeverity.Advisory
                ? nameof(FindingSeverity.Advisory)
                : nameof(FindingSeverity.Blocking));
}

[JsonConverter(typeof(FindingCategoryJsonConverter))]
public enum FindingCategory
{
    Unspecified,
    SpecCompliance,
    SpecDefect,
    Correctness,
    TestEvidence,
    TestCoverage,
    CodeQuality,
    OperatorOwned
}

public sealed class FindingCategoryJsonConverter : JsonConverter<FindingCategory>
{
    private static readonly IReadOnlyDictionary<string, FindingCategory> Categories =
        new Dictionary<string, FindingCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["spec-compliance"] = FindingCategory.SpecCompliance,
            ["spec-defect"] = FindingCategory.SpecDefect,
            ["correctness"] = FindingCategory.Correctness,
            ["test-evidence"] = FindingCategory.TestEvidence,
            ["test-coverage"] = FindingCategory.TestCoverage,
            ["code-quality"] = FindingCategory.CodeQuality,
            ["operator-owned"] = FindingCategory.OperatorOwned,
            ["unspecified"] = FindingCategory.Unspecified
        };

    public override bool HandleNull => true;

    public override FindingCategory Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Categories.TryGetValue(reader.GetString() ?? string.Empty, out var category))
        {
            return category;
        }

        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
        {
            using var ignored = JsonDocument.ParseValue(ref reader);
        }

        return FindingCategory.Unspecified;
    }

    public override void Write(
        Utf8JsonWriter writer,
        FindingCategory value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWireValue(value));

    public static string ToWireValue(FindingCategory value) => value switch
    {
        FindingCategory.SpecCompliance => "spec-compliance",
        FindingCategory.SpecDefect => "spec-defect",
        FindingCategory.Correctness => "correctness",
        FindingCategory.TestEvidence => "test-evidence",
        FindingCategory.TestCoverage => "test-coverage",
        FindingCategory.CodeQuality => "code-quality",
        FindingCategory.OperatorOwned => "operator-owned",
        _ => "unspecified"
    };
}

public sealed record ReviewFindingLocation(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("hunk")] string? Hunk = null)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Hunk)
            ? $"{File}::{Region}"
            : $"{File}::{Region} [{Hunk}]";
}

public sealed record FindingEvidenceSelection(
    [property: JsonPropertyName("test_project")] string TestProject,
    [property: JsonPropertyName("test_class")] string TestClass);

public sealed record FindingEvidenceRequest(
    [property: JsonPropertyName("selections")] IReadOnlyList<FindingEvidenceSelection> Selections);

[JsonConverter(typeof(JsonStringEnumConverter<FindingEvidenceArm>))]
public enum FindingEvidenceArm
{
    Candidate,
    Baseline
}

[JsonConverter(typeof(FindingEvidenceArmDispositionJsonConverter))]
public enum FindingEvidenceArmDisposition
{
    Inconclusive,
    ApparatusFailure,
    Green,
    Red
}

public sealed class FindingEvidenceArmDispositionJsonConverter : JsonConverter<FindingEvidenceArmDisposition>
{
    public override FindingEvidenceArmDisposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value.ToLowerInvariant() switch
            {
                "green" => FindingEvidenceArmDisposition.Green,
                "red" => FindingEvidenceArmDisposition.Red,
                "apparatus-failure" => FindingEvidenceArmDisposition.ApparatusFailure,
                _ => FindingEvidenceArmDisposition.Inconclusive
            }
            : FindingEvidenceArmDisposition.Inconclusive;

    public override void Write(Utf8JsonWriter writer, FindingEvidenceArmDisposition value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            FindingEvidenceArmDisposition.Green => "green",
            FindingEvidenceArmDisposition.Red => "red",
            FindingEvidenceArmDisposition.ApparatusFailure => "apparatus-failure",
            _ => "inconclusive"
        });
}

[JsonConverter(typeof(FindingEvidenceOutcomeReasonJsonConverter))]
public enum FindingEvidenceOutcomeReason
{
    Unknown,
    ValidEvidence,
    VacuousEvidence,
    ApparatusFailure,
    CandidateRed,
    CandidateInconclusive,
    BaselineInconclusive
}

public sealed class FindingEvidenceOutcomeReasonJsonConverter : JsonConverter<FindingEvidenceOutcomeReason>
{
    private static readonly IReadOnlyDictionary<string, FindingEvidenceOutcomeReason> Reasons =
        new Dictionary<string, FindingEvidenceOutcomeReason>(StringComparer.OrdinalIgnoreCase)
        {
            ["valid-evidence"] = FindingEvidenceOutcomeReason.ValidEvidence,
            ["vacuous-evidence"] = FindingEvidenceOutcomeReason.VacuousEvidence,
            ["apparatus-failure"] = FindingEvidenceOutcomeReason.ApparatusFailure,
            ["candidate-red"] = FindingEvidenceOutcomeReason.CandidateRed,
            ["candidate-inconclusive"] = FindingEvidenceOutcomeReason.CandidateInconclusive,
            ["baseline-inconclusive"] = FindingEvidenceOutcomeReason.BaselineInconclusive,
            ["unknown"] = FindingEvidenceOutcomeReason.Unknown
        };

    public override FindingEvidenceOutcomeReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Reasons.TryGetValue(reader.GetString() ?? string.Empty, out var reason)
            ? reason
            : FindingEvidenceOutcomeReason.Unknown;

    public override void Write(Utf8JsonWriter writer, FindingEvidenceOutcomeReason value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWireValue(value));

    public static string ToWireValue(FindingEvidenceOutcomeReason value) => value switch
    {
        FindingEvidenceOutcomeReason.ValidEvidence => "valid-evidence",
        FindingEvidenceOutcomeReason.VacuousEvidence => "vacuous-evidence",
        FindingEvidenceOutcomeReason.ApparatusFailure => "apparatus-failure",
        FindingEvidenceOutcomeReason.CandidateRed => "candidate-red",
        FindingEvidenceOutcomeReason.CandidateInconclusive => "candidate-inconclusive",
        FindingEvidenceOutcomeReason.BaselineInconclusive => "baseline-inconclusive",
        _ => "unknown"
    };
}

[JsonConverter(typeof(FindingEvidenceNotHonouredReasonJsonConverter))]
public enum FindingEvidenceNotHonouredReason
{
    Unknown,
    UnsupportedProject,
    UnparseableSelection,
    PerRoundCap,
    CandidateShaMissing,
    ExecutorUnavailable,
    SelectionApparatusFailure,
    RunFailed
}

public sealed class FindingEvidenceNotHonouredReasonJsonConverter : JsonConverter<FindingEvidenceNotHonouredReason>
{
    private static readonly IReadOnlyDictionary<string, FindingEvidenceNotHonouredReason> Reasons =
        new Dictionary<string, FindingEvidenceNotHonouredReason>(StringComparer.OrdinalIgnoreCase)
        {
            ["unknown"] = FindingEvidenceNotHonouredReason.Unknown,
            ["unsupported-project"] = FindingEvidenceNotHonouredReason.UnsupportedProject,
            ["unparseable-selection"] = FindingEvidenceNotHonouredReason.UnparseableSelection,
            ["per-round-cap"] = FindingEvidenceNotHonouredReason.PerRoundCap,
            ["candidate-sha-missing"] = FindingEvidenceNotHonouredReason.CandidateShaMissing,
            ["executor-unavailable"] = FindingEvidenceNotHonouredReason.ExecutorUnavailable,
            ["selection-apparatus-failure"] = FindingEvidenceNotHonouredReason.SelectionApparatusFailure,
            ["run-failed"] = FindingEvidenceNotHonouredReason.RunFailed
        };

    public override FindingEvidenceNotHonouredReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Reasons.TryGetValue(reader.GetString() ?? string.Empty, out var reason)
            ? reason
            : FindingEvidenceNotHonouredReason.Unknown;

    public override void Write(Utf8JsonWriter writer, FindingEvidenceNotHonouredReason value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWireValue(value));

    public static string ToWireValue(FindingEvidenceNotHonouredReason value) => value switch
    {
        FindingEvidenceNotHonouredReason.UnsupportedProject => "unsupported-project",
        FindingEvidenceNotHonouredReason.UnparseableSelection => "unparseable-selection",
        FindingEvidenceNotHonouredReason.PerRoundCap => "per-round-cap",
        FindingEvidenceNotHonouredReason.CandidateShaMissing => "candidate-sha-missing",
        FindingEvidenceNotHonouredReason.ExecutorUnavailable => "executor-unavailable",
        FindingEvidenceNotHonouredReason.SelectionApparatusFailure => "selection-apparatus-failure",
        FindingEvidenceNotHonouredReason.RunFailed => "run-failed",
        _ => "unknown"
    };
}

public sealed record FindingEvidenceOutcome(
    [property: JsonPropertyName("honoured")] bool Honoured,
    [property: JsonPropertyName("receipt_id")] string? ReceiptId = null,
    [property: JsonPropertyName("reason")] FindingEvidenceNotHonouredReason? Reason = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("result_reason")] FindingEvidenceOutcomeReason? ResultReason = null);

public sealed record FindingEvidenceArmReceipt(
    FindingEvidenceArm Arm,
    string Sha,
    FindingEvidenceArmDisposition Disposition,
    bool Accepted,
    bool Passed,
    string Summary,
    IReadOnlyList<string>? ReceiptPaths = null,
    IReadOnlyList<string>? FailingTestIdentities = null);

public sealed record FindingEvidenceReceipt(
    string ReceiptId,
    string CandidateSha,
    FindingEvidenceRequest Request,
    bool Accepted,
    bool Passed,
    string Summary,
    IReadOnlyList<FindingEvidenceArmReceipt>? Arms = null);

public sealed record ReviewFinding(
    [property: JsonPropertyName("stable_id")] string StableId,
    [property: JsonPropertyName("state")] ReviewFindingState State,
    [property: JsonPropertyName("location")] ReviewFindingLocation Location,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("severity")]
    [property: JsonConverter(typeof(FindingSeverityJsonConverter))]
    FindingSeverity Severity = FindingSeverity.Blocking,
    [property: JsonPropertyName("category")]
    [property: JsonConverter(typeof(FindingCategoryJsonConverter))]
    FindingCategory Category = FindingCategory.Unspecified,
    [property: JsonPropertyName("evidence_request")] FindingEvidenceRequest? EvidenceRequest = null,
    [property: JsonPropertyName("evidence_outcome")] FindingEvidenceOutcome? EvidenceOutcome = null);

public sealed record ReviewFindingRound(
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<ReviewFindingLocation> TouchedAnchors,
    string? TouchProofDiagnostic = null);

public sealed record ReviewFindingIdentityMismatch(
    string Code,
    string Message,
    string PriorStableId,
    string SubmittedStableId,
    ReviewFindingLocation PriorLocation,
    ReviewFindingLocation SubmittedLocation);

public sealed record ReviewFindingContractViolation(
    string Code,
    string Message,
    string? PriorStableId = null,
    string? SubmittedStableId = null,
    ReviewFindingLocation? PriorLocation = null,
    ReviewFindingLocation? SubmittedLocation = null,
    IReadOnlyList<ReviewFindingIdentityMismatch>? IdentityMismatches = null);

public sealed record ReviewFindingIdentityCanonicalization(
    string PriorStableId,
    string SubmittedStableId,
    ReviewFindingLocation Anchor);

public sealed class ReviewFindingConvergenceException : InvalidOperationException
{
    public ReviewFindingConvergenceException(
        string code,
        int previousOpenCount,
        int nextOpenCount,
        string message)
        : this(
            code,
            previousOpenCount,
            nextOpenCount,
            message,
            new ReviewFindingContractViolation(code, message))
    {
    }

    public ReviewFindingConvergenceException(
        string code,
        int previousOpenCount,
        int nextOpenCount,
        string message,
        ReviewFindingContractViolation violation)
        : base(message)
    {
        Code = code;
        PreviousOpenCount = previousOpenCount;
        NextOpenCount = nextOpenCount;
        Violation = violation ?? throw new ArgumentNullException(nameof(violation));
    }

    public string Code { get; }

    public int PreviousOpenCount { get; }

    public int NextOpenCount { get; }

    public ReviewFindingContractViolation Violation { get; }
}

public static class ReviewFindings
{
    public static IReadOnlyList<ReviewFinding> GetEffectiveOpenFindings(
        IEnumerable<ReviewFinding> findings,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections)
    {
        TryGetEffectiveOpenFindings(
            findings,
            criteriaCorrections,
            out var effectiveFindings,
            out _);
        return effectiveFindings;
    }

    public static bool TryGetEffectiveOpenFindings(
        IEnumerable<ReviewFinding> findings,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections,
        out IReadOnlyList<ReviewFinding> effectiveFindings,
        out IReadOnlyList<(ReviewFinding Finding, EffectiveAcceptanceCriteriaCorrection Correction)> suppressedFindings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(criteriaCorrections);

        var result = FilterWaived(
            findings.Where(finding => finding.State == ReviewFindingState.Open),
            finding => finding.Description,
            criteriaCorrections);
        effectiveFindings = result.Kept;
        suppressedFindings = result.Suppressed
            .Select(item => (item.Item, item.Correction))
            .ToArray();
        return suppressedFindings.Count > 0;
    }

    public static IReadOnlyList<ReviewFinding> GetOpenBlockingFindings(
        IEnumerable<ReviewFinding> findings,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections) =>
        GetEffectiveOpenFindings(findings, criteriaCorrections)
            .Where(finding => finding.Severity == FindingSeverity.Blocking)
            .ToArray();

    public static IReadOnlyList<ReviewFinding> GetOpenAdvisoryFindings(
        IEnumerable<ReviewFinding> findings,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections) =>
        GetEffectiveOpenFindings(findings, criteriaCorrections)
            .Where(finding => finding.Severity == FindingSeverity.Advisory)
            .ToArray();

    public static bool TryFilterWaivedDescriptions(
        IEnumerable<string> findings,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections,
        out IReadOnlyList<string> effectiveFindings,
        out IReadOnlyList<(string Finding, EffectiveAcceptanceCriteriaCorrection Correction)> suppressedFindings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(criteriaCorrections);

        var result = FilterWaived(findings, finding => finding, criteriaCorrections);
        effectiveFindings = result.Kept;
        suppressedFindings = result.Suppressed
            .Select(item => (item.Item, item.Correction))
            .ToArray();
        return suppressedFindings.Count > 0;
    }

    public static bool IsWaived(
        string finding,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections)
    {
        TryFilterWaivedDescriptions(
            [finding],
            criteriaCorrections,
            out _,
            out var suppressedFindings);
        return suppressedFindings.Count > 0;
    }

    private static FilteredFindings<T> FilterWaived<T>(
        IEnumerable<T> findings,
        Func<T, string> description,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections)
    {
        var kept = new List<T>();
        var suppressed = new List<(T Item, EffectiveAcceptanceCriteriaCorrection Correction)>();
        foreach (var finding in findings)
        {
            if (EffectiveAcceptanceCriteriaCorrectionParser.TryFindMatchingCorrection(
                description(finding),
                criteriaCorrections,
                out var correction))
            {
                suppressed.Add((finding, correction));
                continue;
            }

            kept.Add(finding);
        }

        return new FilteredFindings<T>(kept, suppressed);
    }

    private sealed record FilteredFindings<T>(
        IReadOnlyList<T> Kept,
        IReadOnlyList<(T Item, EffectiveAcceptanceCriteriaCorrection Correction)> Suppressed);
}

public static class ReviewFindingConvergence
{
    public const string IdentityMovedViolationCode = "ERR_REVIEW_FINDING_IDENTITY_MOVED";
    public const string UntouchedReopenViolationCode = "ERR_REVIEW_FINDING_UNTOUCHED_REOPEN";
    public const string RecycledAnchorIdentityViolationCode = "ERR_REVIEW_FINDING_ANCHOR_IDENTITY_RECYCLED";
    public const string NeedsWorkWithoutOpenFindingsViolationCode = "ERR_REVIEW_NEEDS_WORK_WITHOUT_OPEN_FINDINGS";
    public const string UnprovenResolutionAtCapViolationCode = "ERR_REVIEW_FINDING_UNPROVEN_RESOLUTION_AT_CAP";

    public const string MissingReviewRetryCapReceiptViolationCode = "ERR_REVIEW_FINDING_MISSING_CAP_RECEIPT";

    public static IReadOnlyList<ReviewFinding> ApplyRound(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound nextRound) =>
        ApplyRound(previous, nextRound, out _);

    public static IReadOnlyList<ReviewFinding> ApplyRound(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound nextRound,
        out IReadOnlyList<ReviewFindingIdentityCanonicalization> canonicalizations)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(nextRound);

        ValidateUniqueStableIds(previous, "previous");
        ValidateUniqueStableIds(nextRound.Findings, "next");

        var submittedFindings = CanonicalizeLoneNewIdentity(previous, nextRound.Findings, out canonicalizations);
        var identityMismatches = CollectIdentityMismatches(
            previous,
            submittedFindings,
            nextRound.TouchedAnchors,
            nextRound.TouchProofDiagnostic);
        if (identityMismatches.Count > 0)
        {
            var first = identityMismatches[0];
            throw new ReviewFindingConvergenceException(
                first.Code,
                CountOpen(previous),
                CountOpen(submittedFindings),
                first.Message,
                new ReviewFindingContractViolation(
                    first.Code,
                    first.Message,
                    first.PriorStableId,
                    first.SubmittedStableId,
                    first.PriorLocation,
                    first.SubmittedLocation,
                    identityMismatches));
        }

        var nextById = submittedFindings.ToDictionary(finding => finding.StableId, StringComparer.Ordinal);
        var merged = new List<ReviewFinding>(Math.Max(previous.Count, nextRound.Findings.Count));
        foreach (var prior in previous)
        {
            if (!nextById.Remove(prior.StableId, out var submitted))
            {
                merged.Add(prior);
                continue;
            }

            var anchorMoved = !SameAnchor(prior.Location, submitted.Location);

            if (prior.State == ReviewFindingState.Resolved &&
                submitted.State == ReviewFindingState.Open)
            {
                if (!AnchorWasTouched(prior.Location, nextRound.TouchedAnchors))
                {
                    throw new ReviewFindingConvergenceException(
                        UntouchedReopenViolationCode,
                        CountOpen(previous),
                        CountOpen(submittedFindings),
                        BuildUntouchedReopenMessage(prior.StableId, nextRound.TouchProofDiagnostic),
                        new ReviewFindingContractViolation(
                            UntouchedReopenViolationCode,
                            BuildUntouchedReopenMessage(prior.StableId, nextRound.TouchProofDiagnostic),
                            prior.StableId,
                            prior.StableId,
                            prior.Location,
                            submitted.Location));
                }
            }

            // A still-open finding may follow code that moved when the round diff proves the old
            // anchor was touched. Keep the submitted location so the stable identity follows the
            // defect instead of remaining permanently bound to its first presentation location.
            // Resolved findings retain their original anchor for regression-reopen protection.
            var mergedFinding = anchorMoved && submitted.State == ReviewFindingState.Resolved
                ? submitted with { Location = prior.Location }
                : submitted;
            if (prior.EvidenceOutcome is not null &&
                (submitted.EvidenceRequest is null || SameRequest(prior.EvidenceRequest, submitted.EvidenceRequest)))
            {
                mergedFinding = mergedFinding with
                {
                    EvidenceRequest = submitted.EvidenceRequest ?? prior.EvidenceRequest,
                    EvidenceOutcome = prior.EvidenceOutcome
                };
            }
            merged.Add(mergedFinding);
        }

        foreach (var newFinding in nextById.Values)
        {
            merged.Add(newFinding);
        }

        // Genuinely NEW stable_ids at fresh anchors may grow the open set: a reviewer discovering a real
        // defect late is doing its job, and rejecting the growth traps it — the structured report would
        // fail an open-set-increase check, a prose-only needs-work fails the no-open-findings rule, and
        // pass would be dishonest. (That trap cost four review rounds on 2026-07-25 before the increase
        // check was removed.) Anchor identity is file+region: hunk line-ranges drift whenever upstream
        // code is edited, and requiring hunk equality rejected honest re-reports of carried findings six
        // rounds in a row on 2026-07-27. Recycling is likewise scoped to OPEN priors — a resolved
        // finding's anchor must be able to host a genuinely new defect under a new id, or that defect
        // becomes unreportable under any id (the R8/R9 circular trap). Re-litigation abuse stays blocked
        // by the remaining guards: a still-open id cannot move file/region unless the round diff proves
        // its prior anchor was touched, a resolved id cannot reopen without the same proof, and an OPEN
        // finding's exact anchor cannot be re-keyed.
        return merged
            .OrderBy(finding => finding.StableId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool IsRejectedCapResolutionRound(ReviewFindingContractViolation violation) =>
        violation.Code is UnprovenResolutionAtCapViolationCode or MissingReviewRetryCapReceiptViolationCode;

    internal static bool IsRejectedCapResolutionTransition(
        ReviewFindingContractViolation violation,
        string submittedStableId) =>
        IsRejectedCapResolutionRound(violation) &&
        (string.Equals(violation.SubmittedStableId, submittedStableId, StringComparison.Ordinal) ||
         (violation.IdentityMismatches ?? []).Any(mismatch =>
             string.Equals(mismatch.SubmittedStableId, submittedStableId, StringComparison.Ordinal)));

    internal static IReadOnlyList<ReviewFinding> ApplyRejectedCapResolutionRound(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound rejectedRound,
        ReviewFindingContractViolation violation)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(rejectedRound);
        ArgumentNullException.ThrowIfNull(violation);

        if (!IsRejectedCapResolutionRound(violation))
        {
            throw new ArgumentException(
                $"Violation '{violation.Code}' is not a rejected review-cap resolution.",
                nameof(violation));
        }

        var acceptedFindings = rejectedRound.Findings
            .Where(finding => !IsRejectedCapResolutionTransition(violation, finding.StableId))
            .ToArray();

        // Retain the prior form of only the rejected transitions. Fresh findings and every other valid
        // transition in the round remain authoritative, so a cap rejection cannot hide newly discovered
        // blockers while preventing an unproven resolution from poisoning later replay.
        return ApplyRound(previous, rejectedRound with { Findings = acceptedFindings });
    }

    internal static bool IsRejectedIdentityTransitionRound(ReviewFindingContractViolation violation) =>
        violation.Code is IdentityMovedViolationCode or RecycledAnchorIdentityViolationCode;

    internal static bool IsRejectedIdentityTransition(
        ReviewFindingContractViolation violation,
        string submittedStableId) =>
        IsRejectedIdentityTransitionRound(violation) &&
        (string.Equals(violation.SubmittedStableId, submittedStableId, StringComparison.Ordinal) ||
         (violation.IdentityMismatches ?? []).Any(mismatch =>
             string.Equals(mismatch.SubmittedStableId, submittedStableId, StringComparison.Ordinal)));

    internal static bool CanCanonicalizeIdentityTransitions(ReviewFindingContractViolation violation) =>
        IsRejectedIdentityTransitionRound(violation) &&
        (violation.IdentityMismatches ?? []).Count > 0 &&
        violation.IdentityMismatches!.All(mismatch =>
            mismatch.Code == IdentityMovedViolationCode &&
            string.Equals(mismatch.PriorStableId, mismatch.SubmittedStableId, StringComparison.Ordinal));

    internal static IReadOnlyList<ReviewFinding> ApplyCanonicalizedIdentityTransitionRound(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound round,
        ReviewFindingContractViolation violation,
        out IReadOnlyList<ReviewFindingIdentityCanonicalization> canonicalizations)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(violation);

        if (!CanCanonicalizeIdentityTransitions(violation))
        {
            throw new ArgumentException(
                $"Violation '{violation.Code}' cannot be canonicalized to prior identity anchors.",
                nameof(violation));
        }

        var mismatches = violation.IdentityMismatches!
            .ToDictionary(mismatch => mismatch.SubmittedStableId, StringComparer.Ordinal);
        canonicalizations = mismatches.Values
            .Select(mismatch => new ReviewFindingIdentityCanonicalization(
                mismatch.PriorStableId,
                mismatch.SubmittedStableId,
                mismatch.PriorLocation))
            .OrderBy(item => item.PriorStableId, StringComparer.Ordinal)
            .ToArray();
        var canonicalizedFindings = round.Findings
            .Select(finding => mismatches.TryGetValue(finding.StableId, out var mismatch)
                ? finding with { Location = mismatch.PriorLocation }
                : finding)
            .ToArray();

        return ApplyRound(previous, round with { Findings = canonicalizedFindings });
    }

    internal static IReadOnlyList<ReviewFinding> ApplyRejectedIdentityTransitionRound(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound rejectedRound,
        ReviewFindingContractViolation violation)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(rejectedRound);
        ArgumentNullException.ThrowIfNull(violation);

        if (!IsRejectedIdentityTransitionRound(violation))
        {
            throw new ArgumentException(
                $"Violation '{violation.Code}' is not a rejected identity transition.",
                nameof(violation));
        }

        var acceptedFindings = rejectedRound.Findings
            .Where(finding => !IsRejectedIdentityTransition(violation, finding.StableId))
            .ToArray();

        // Reject only the submitted anchor transition. The prior form remains in the ledger while
        // unrelated findings and transitions in the same substantive round remain authoritative.
        return ApplyRound(previous, rejectedRound with { Findings = acceptedFindings });
    }

    public static void ValidateResolutionAtCap(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound nextRound,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections,
        string? reviewedCommit,
        IReadOnlyList<FindingEvidenceReceipt> evidenceReceipts)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(nextRound);
        ArgumentNullException.ThrowIfNull(criteriaCorrections);
        ArgumentNullException.ThrowIfNull(evidenceReceipts);

        var submittedById = nextRound.Findings.ToDictionary(finding => finding.StableId, StringComparer.Ordinal);
        var unproven = ReviewFindings.GetOpenBlockingFindings(previous, criteriaCorrections)
            .Where(prior =>
                submittedById.TryGetValue(prior.StableId, out var submitted) &&
                submitted.State == ReviewFindingState.Resolved &&
                !HasAuthoritativeCapResolutionProof(
                    prior,
                    nextRound.TouchedAnchors,
                    reviewedCommit,
                    evidenceReceipts))
            .OrderBy(finding => finding.StableId, StringComparer.Ordinal)
            .ToArray();
        if (unproven.Length == 0)
        {
            return;
        }

        var stableIds = string.Join(", ", unproven.Select(finding => finding.StableId));
        var candidate = string.IsNullOrWhiteSpace(reviewedCommit) ? "missing" : reviewedCommit.Trim();
        var message =
            $"At the recorded review-retry cap, open blocking stable_id(s) {stableIds} were submitted as resolved " +
            $"without a system-confirmed touched anchor, valid focused-evidence receipt bound to candidate {candidate}, " +
            "or an active operator waiver. The prior open ledger was retained for operator decision.";
        var mismatches = unproven.Select(finding => new ReviewFindingIdentityMismatch(
            UnprovenResolutionAtCapViolationCode,
            message,
            finding.StableId,
            finding.StableId,
            finding.Location,
            submittedById[finding.StableId].Location)).ToArray();
        var first = unproven[0];
        throw new ReviewFindingConvergenceException(
            UnprovenResolutionAtCapViolationCode,
            CountOpen(previous),
            CountOpen(nextRound.Findings),
            message,
            new ReviewFindingContractViolation(
                UnprovenResolutionAtCapViolationCode,
                message,
                first.StableId,
                first.StableId,
                first.Location,
                submittedById[first.StableId].Location,
                mismatches));
    }

    public static void ValidateResolutionWithoutCapReceipt(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound nextRound,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections,
        string? reviewedCommit,
        IReadOnlyList<FindingEvidenceReceipt> evidenceReceipts)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(nextRound);
        ArgumentNullException.ThrowIfNull(criteriaCorrections);
        ArgumentNullException.ThrowIfNull(evidenceReceipts);

        var submittedById = nextRound.Findings.ToDictionary(finding => finding.StableId, StringComparer.Ordinal);
        var unproven = ReviewFindings.GetOpenBlockingFindings(previous, criteriaCorrections)
            .Where(prior =>
                submittedById.TryGetValue(prior.StableId, out var submitted) &&
                submitted.State == ReviewFindingState.Resolved &&
                !HasAuthoritativeCapResolutionProof(
                    prior,
                    nextRound.TouchedAnchors,
                    reviewedCommit,
                    evidenceReceipts))
            .OrderBy(finding => finding.StableId, StringComparer.Ordinal)
            .ToArray();
        if (unproven.Length == 0)
        {
            return;
        }

        var stableIds = string.Join(", ", unproven.Select(finding => finding.StableId));
        var candidate = string.IsNullOrWhiteSpace(reviewedCommit) ? "missing" : reviewedCommit.Trim();
        var message =
            $"Reviewer dispatch is missing its system-owned review-retry cap receipt, so open blocking stable_id(s) {stableIds} " +
            "cannot be safely classified against the configured cap. They were submitted as resolved without a system-confirmed " +
            $"touched anchor, valid focused-evidence receipt bound to candidate {candidate}, or active operator waiver. " +
            "The prior open ledger was retained for operator decision.";
        var mismatches = unproven.Select(finding => new ReviewFindingIdentityMismatch(
            MissingReviewRetryCapReceiptViolationCode,
            message,
            finding.StableId,
            finding.StableId,
            finding.Location,
            submittedById[finding.StableId].Location)).ToArray();
        var first = unproven[0];
        throw new ReviewFindingConvergenceException(
            MissingReviewRetryCapReceiptViolationCode,
            CountOpen(previous),
            CountOpen(nextRound.Findings),
            message,
            new ReviewFindingContractViolation(
                MissingReviewRetryCapReceiptViolationCode,
                message,
                first.StableId,
                first.StableId,
                first.Location,
                submittedById[first.StableId].Location,
                mismatches));
    }

    private static bool HasAuthoritativeCapResolutionProof(
        ReviewFinding prior,
        IReadOnlyList<ReviewFindingLocation> touchedAnchors,
        string? reviewedCommit,
        IReadOnlyList<FindingEvidenceReceipt> evidenceReceipts)
    {
        if (string.IsNullOrWhiteSpace(reviewedCommit))
        {
            return false;
        }

        if (AnchorWasTouched(prior.Location, touchedAnchors))
        {
            return true;
        }

        var outcome = prior.EvidenceOutcome;
        return outcome is { Honoured: true, ReceiptId.Length: > 0, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence } &&
            evidenceReceipts.Any(receipt =>
                string.Equals(receipt.ReceiptId, outcome.ReceiptId, StringComparison.Ordinal) &&
                string.Equals(receipt.CandidateSha, reviewedCommit, StringComparison.OrdinalIgnoreCase) &&
                receipt.Accepted &&
                receipt.Passed);
    }

    public static ReviewFinding? ResolveMergedFinding(
        IReadOnlyList<ReviewFinding> mergedFindings,
        ReviewFindingRound reportedRound,
        string submittedStableId)
    {
        ArgumentNullException.ThrowIfNull(mergedFindings);
        ArgumentNullException.ThrowIfNull(reportedRound);
        ArgumentException.ThrowIfNullOrWhiteSpace(submittedStableId);

        var exact = mergedFindings.FirstOrDefault(finding =>
            string.Equals(finding.StableId, submittedStableId, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        var reportedFinding = reportedRound.Findings.FirstOrDefault(finding =>
            string.Equals(finding.StableId, submittedStableId, StringComparison.Ordinal));
        if (reportedFinding is null)
        {
            return null;
        }

        // CanonicalizeLoneNewIdentity is the only convergence path that intentionally changes an id.
        // It replaces the submitted id with the one omitted from the raw round at the exact same anchor.
        var submittedIds = reportedRound.Findings
            .Select(finding => finding.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var canonicalizedMatches = mergedFindings
            .Where(finding =>
                !submittedIds.Contains(finding.StableId) &&
                ExactAnchor(finding.Location, reportedFinding.Location))
            .Take(2)
            .ToArray();
        return canonicalizedMatches.Length == 1 ? canonicalizedMatches[0] : null;
    }

    private static bool SameRequest(FindingEvidenceRequest? left, FindingEvidenceRequest? right)
    {
        if (left?.Selections is null || right?.Selections is null)
        {
            return false;
        }

        static string[] Keys(FindingEvidenceRequest request) => request.Selections
            .Where(selection =>
                selection is not null &&
                !string.IsNullOrWhiteSpace(selection.TestProject) &&
                !string.IsNullOrWhiteSpace(selection.TestClass))
            .Select(selection => $"{selection.TestProject.Trim().ToUpperInvariant()}:{selection.TestClass.Trim()}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return Keys(left).SequenceEqual(Keys(right), StringComparer.Ordinal);
    }

    private static IReadOnlyList<ReviewFindingIdentityMismatch> CollectIdentityMismatches(
        IReadOnlyList<ReviewFinding> previous,
        IReadOnlyList<ReviewFinding> submitted,
        IReadOnlyList<ReviewFindingLocation> touchedAnchors,
        string? touchProofDiagnostic)
    {
        var submittedById = submitted.ToDictionary(finding => finding.StableId, StringComparer.Ordinal);
        var previousIds = previous
            .Select(finding => finding.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var mismatches = new List<ReviewFindingIdentityMismatch>();

        foreach (var prior in previous)
        {
            if (!submittedById.TryGetValue(prior.StableId, out var next) ||
                next.State != ReviewFindingState.Open ||
                SameAnchor(prior.Location, next.Location) ||
                AnchorWasTouched(prior.Location, touchedAnchors))
            {
                continue;
            }

            var message = BuildIdentityMovedMessage(
                prior.StableId,
                prior.Location,
                next.Location,
                touchProofDiagnostic);
            mismatches.Add(new ReviewFindingIdentityMismatch(
                IdentityMovedViolationCode,
                message,
                prior.StableId,
                next.StableId,
                prior.Location,
                next.Location));
        }

        foreach (var newFinding in submitted.Where(finding => !previousIds.Contains(finding.StableId)))
        {
            var priorAtAnchor = previous.FirstOrDefault(prior =>
                prior.State == ReviewFindingState.Open &&
                ExactAnchor(prior.Location, newFinding.Location));
            if (priorAtAnchor is null)
            {
                continue;
            }

            var message = $"Structural anchor '{newFinding.Location}' already belongs to open stable_id '{priorAtAnchor.StableId}'; it cannot be recycled as '{newFinding.StableId}'.";
            mismatches.Add(new ReviewFindingIdentityMismatch(
                RecycledAnchorIdentityViolationCode,
                message,
                priorAtAnchor.StableId,
                newFinding.StableId,
                priorAtAnchor.Location,
                newFinding.Location));
        }

        return mismatches
            .OrderBy(mismatch => mismatch.PriorStableId, StringComparer.Ordinal)
            .ThenBy(mismatch => mismatch.SubmittedStableId, StringComparer.Ordinal)
            .ThenBy(mismatch => mismatch.Code, StringComparer.Ordinal)
            .ThenBy(mismatch => mismatch.PriorLocation.File, StringComparer.Ordinal)
            .ThenBy(mismatch => mismatch.PriorLocation.Region, StringComparer.Ordinal)
            .ThenBy(mismatch => mismatch.PriorLocation.Hunk, StringComparer.Ordinal)
            .ThenBy(mismatch => mismatch.SubmittedLocation.File, StringComparer.Ordinal)
            .ThenBy(mismatch => mismatch.SubmittedLocation.Region, StringComparer.Ordinal)
            .ThenBy(mismatch => mismatch.SubmittedLocation.Hunk, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ReviewFinding> CanonicalizeLoneNewIdentity(
        IReadOnlyList<ReviewFinding> previous,
        IReadOnlyList<ReviewFinding> submitted,
        out IReadOnlyList<ReviewFindingIdentityCanonicalization> canonicalizations)
    {
        canonicalizations = [];
        var previousIds = previous
            .Select(finding => finding.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var submittedIds = submitted
            .Select(finding => finding.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var omittedOpen = previous
            .Where(finding =>
                finding.State == ReviewFindingState.Open &&
                !submittedIds.Contains(finding.StableId))
            .ToArray();
        var newlyNamed = submitted
            .Where(finding => !previousIds.Contains(finding.StableId))
            .ToArray();
        if (omittedOpen.Length != 1 ||
            newlyNamed.Length != 1 ||
            !ExactAnchor(omittedOpen[0].Location, newlyNamed[0].Location))
        {
            return submitted;
        }

        var prior = omittedOpen[0];
        var replacement = newlyNamed[0];
        canonicalizations =
        [
            new ReviewFindingIdentityCanonicalization(
                prior.StableId,
                replacement.StableId,
                prior.Location)
        ];
        return submitted
            .Select(finding =>
                ReferenceEquals(finding, replacement)
                    ? finding with { StableId = prior.StableId, Location = prior.Location }
                    : finding)
            .ToArray();
    }

    public static int CountOpen(IEnumerable<ReviewFinding> findings) =>
        findings.Count(finding => finding.State == ReviewFindingState.Open);

    public static bool TryParseJson(
        string findingsJson,
        string touchedAnchorsJson,
        out ReviewFindingRound round,
        out string diagnostic)
    {
        round = new ReviewFindingRound([], []);
        diagnostic = string.Empty;
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

            var findings = JsonSerializer.Deserialize<List<ReviewFinding>>(findingsJson, options);
            var touchedAnchors = JsonSerializer.Deserialize<List<ReviewFindingLocation>>(touchedAnchorsJson, options);
            if (findings is null || touchedAnchors is null)
            {
                diagnostic = "findings or touched_anchors JSON was null.";
                return false;
            }

            ValidateFindings(findings);
            ValidateAnchors(touchedAnchors);
            ValidateUniqueStableIds(findings, "reviewer");
            round = new ReviewFindingRound(findings, touchedAnchors);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            diagnostic = ex.Message;
            return false;
        }
    }

    private static bool AnchorWasTouched(
        ReviewFindingLocation anchor,
        IReadOnlyList<ReviewFindingLocation> touchedAnchors) =>
        touchedAnchors.Any(touched => SameAnchor(anchor, touched));

    // Identity-level anchor equality: file + canonical region only. Region text is reviewer-authored,
    // so harmless signature, qualification, whitespace, and casing paraphrases share an identity.
    // Raw submitted location text remains on the merged finding; normalization is comparison-only.
    private static bool SameAnchor(ReviewFindingLocation left, ReviewFindingLocation right)
    {
        if (!string.Equals(left.File, right.File, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(left.Region, right.Region, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(
            NormalizeRegion(left.Region),
            NormalizeRegion(right.Region),
            StringComparison.Ordinal);
    }

    private static string NormalizeRegion(string region)
    {
        var normalized = Regex.Replace(region.Trim(), @"\s+", " ").ToLowerInvariant();
        var withoutLineRange = Regex.Replace(
            normalized,
            @"(?:\s*:\s*\d+(?:\s*-\s*\d+)?|\s*\[\s*(?:lines?\s+)?\d+(?:\s*-\s*\d+)?\s*\]|\s+l\d+\s*-\s*l?\d+|\s+lines?\s+\d+\s*-\s*\d+|(?<!\d)\d+\s*-\s*\d+)\s*$",
            string.Empty).TrimEnd();
        if (withoutLineRange.Length > 0)
        {
            normalized = withoutLineRange;
        }

        var withoutArguments = Regex.Replace(
            normalized,
            @"\s*\(.*\)\s*$",
            string.Empty,
            RegexOptions.Singleline).TrimEnd();
        if (withoutArguments.Length > 0)
        {
            normalized = withoutArguments;
        }

        var withoutTypeParameters = Regex.Replace(normalized, @"<[^<>]*>\s*$", string.Empty).TrimEnd();
        if (withoutTypeParameters.Length > 0)
        {
            normalized = withoutTypeParameters;
        }

        if (!normalized.Contains(' '))
        {
            var finalSeparator = normalized.LastIndexOf('.');
            if (finalSeparator >= 0 && finalSeparator < normalized.Length - 1)
            {
                normalized = normalized[(finalSeparator + 1)..];
            }
        }

        return normalized;
    }

    private static string BuildIdentityMovedMessage(
        string stableId,
        ReviewFindingLocation prior,
        ReviewFindingLocation submitted,
        string? touchProofDiagnostic)
    {
        var message =
            $"Finding '{stableId}' is still open but was reported at a different structural anchor without system-derived proof that its prior anchor was touched; report it at its original anchor, or resolve it and open a new stable_id for a distinct defect." +
            FormatTouchProofDiagnostic(touchProofDiagnostic);
        if (!string.Equals(prior.ToString(), submitted.ToString(), StringComparison.Ordinal))
        {
            return message;
        }

        return message +
            $" Raw locations render identically; normalized_prior_region='{NormalizeRegion(prior.Region)}'; " +
            $"normalized_submitted_region='{NormalizeRegion(submitted.Region)}'.";
    }

    private static string BuildUntouchedReopenMessage(string stableId, string? touchProofDiagnostic) =>
        $"Resolved finding '{stableId}' was re-opened without system-derived proof that its structural anchor was touched." +
        FormatTouchProofDiagnostic(touchProofDiagnostic);

    private static string FormatTouchProofDiagnostic(string? touchProofDiagnostic) =>
        string.IsNullOrWhiteSpace(touchProofDiagnostic)
            ? string.Empty
            : $" Touch-proof diagnostic: {touchProofDiagnostic}";

    // Exact anchor equality (including hunk) — used only by the recycle guard so that a second,
    // distinct defect in the same region but a different hunk stays reportable under a new id.
    private static bool ExactAnchor(ReviewFindingLocation left, ReviewFindingLocation right) =>
        string.Equals(left.File, right.File, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Region, right.Region, StringComparison.Ordinal) &&
        string.Equals(left.Hunk, right.Hunk, StringComparison.Ordinal);

    private static void ValidateFindings(IEnumerable<ReviewFinding> findings)
    {
        foreach (var finding in findings)
        {
            if (string.IsNullOrWhiteSpace(finding.StableId) ||
                string.IsNullOrWhiteSpace(finding.Description) ||
                finding.Location is null ||
                string.IsNullOrWhiteSpace(finding.Location.File) ||
                string.IsNullOrWhiteSpace(finding.Location.Region))
            {
                throw new ArgumentException("Every review finding requires stable_id, state, location.file, location.region, and description.");
            }
        }
    }

    private static void ValidateAnchors(IEnumerable<ReviewFindingLocation> anchors)
    {
        if (anchors.Any(anchor =>
            string.IsNullOrWhiteSpace(anchor.File) ||
            string.IsNullOrWhiteSpace(anchor.Region)))
        {
            throw new ArgumentException("Every touched anchor requires file and region.");
        }
    }

    private static void ValidateUniqueStableIds(IEnumerable<ReviewFinding> findings, string source)
    {
        var duplicate = findings
            .GroupBy(finding => finding.StableId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate stable_id '{duplicate.Key}' in {source} review findings.");
        }
    }
}
