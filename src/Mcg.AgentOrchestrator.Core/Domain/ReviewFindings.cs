using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ReviewFindingState>))]
public enum ReviewFindingState
{
    Open,
    Resolved
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

public sealed record ReviewFinding(
    [property: JsonPropertyName("stable_id")] string StableId,
    [property: JsonPropertyName("state")] ReviewFindingState State,
    [property: JsonPropertyName("location")] ReviewFindingLocation Location,
    [property: JsonPropertyName("description")] string Description);

public sealed record ReviewFindingRound(
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<ReviewFindingLocation> TouchedAnchors);

public sealed class ReviewFindingConvergenceException : InvalidOperationException
{
    public ReviewFindingConvergenceException(
        string code,
        int previousOpenCount,
        int nextOpenCount,
        string message)
        : base(message)
    {
        Code = code;
        PreviousOpenCount = previousOpenCount;
        NextOpenCount = nextOpenCount;
    }

    public string Code { get; }

    public int PreviousOpenCount { get; }

    public int NextOpenCount { get; }
}

public static class ReviewFindingConvergence
{
    public const string IdentityMovedViolationCode = "ERR_REVIEW_FINDING_IDENTITY_MOVED";
    public const string MonotonicityViolationCode = "ERR_REVIEW_FINDING_OPEN_SET_INCREASED";
    public const string UntouchedReopenViolationCode = "ERR_REVIEW_FINDING_UNTOUCHED_REOPEN";
    public const string RecycledAnchorIdentityViolationCode = "ERR_REVIEW_FINDING_ANCHOR_IDENTITY_RECYCLED";

    public static IReadOnlyList<ReviewFinding> ApplyRound(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound nextRound)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(nextRound);

        ValidateUniqueStableIds(previous, "previous");
        ValidateUniqueStableIds(nextRound.Findings, "next");

        var nextById = nextRound.Findings.ToDictionary(finding => finding.StableId, StringComparer.Ordinal);
        var merged = new List<ReviewFinding>(Math.Max(previous.Count, nextRound.Findings.Count));
        foreach (var prior in previous)
        {
            if (!nextById.Remove(prior.StableId, out var submitted))
            {
                merged.Add(prior);
                continue;
            }

            var anchorMoved = !SameAnchor(prior.Location, submitted.Location);
            if (anchorMoved && submitted.State == ReviewFindingState.Open)
            {
                throw new ReviewFindingConvergenceException(
                    IdentityMovedViolationCode,
                    CountOpen(previous),
                    CountOpen(nextRound.Findings),
                    $"Finding '{prior.StableId}' is still open but was reported at a different structural anchor; report it at its original anchor, or resolve it and open a new stable_id for the new anchor.");
            }

            if (prior.State == ReviewFindingState.Resolved &&
                submitted.State == ReviewFindingState.Open)
            {
                if (!AnchorWasTouched(prior.Location, nextRound.TouchedAnchors))
                {
                    throw new ReviewFindingConvergenceException(
                        UntouchedReopenViolationCode,
                        CountOpen(previous),
                        CountOpen(nextRound.Findings),
                        $"Resolved finding '{prior.StableId}' was re-opened without its structural anchor being touched.");
                }
            }

            merged.Add(anchorMoved
                ? submitted with { Location = prior.Location }
                : submitted);
        }

        foreach (var newFinding in nextById.Values)
        {
            var priorAtAnchor = previous.FirstOrDefault(prior =>
                prior.State == ReviewFindingState.Open &&
                ExactAnchor(prior.Location, newFinding.Location));
            if (priorAtAnchor is not null)
            {
                throw new ReviewFindingConvergenceException(
                    RecycledAnchorIdentityViolationCode,
                    CountOpen(previous),
                    CountOpen(nextRound.Findings),
                    $"Structural anchor '{newFinding.Location}' already belongs to open stable_id '{priorAtAnchor.StableId}'; it cannot be recycled as '{newFinding.StableId}'.");
            }

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
        // by the remaining guards: a still-open id cannot move file/region, a resolved id cannot reopen
        // without its anchor being touched, and an OPEN finding's exact anchor cannot be re-keyed.
        return merged
            .OrderBy(finding => finding.StableId, StringComparer.Ordinal)
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

    // Identity-level anchor equality: file + region only. Hunk line-ranges drift whenever code above
    // the finding is edited, so hunk equality must never decide whether a re-reported finding is "the
    // same" one (it rejected six consecutive honest reviews on one goal, 2026-07-27).
    private static bool SameAnchor(ReviewFindingLocation left, ReviewFindingLocation right) =>
        string.Equals(left.File, right.File, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Region, right.Region, StringComparison.Ordinal);

    // Exact anchor equality (including hunk) — used only by the recycle guard so that a second,
    // distinct defect in the same region but a different hunk stays reportable under a new id.
    private static bool ExactAnchor(ReviewFindingLocation left, ReviewFindingLocation right) =>
        SameAnchor(left, right) &&
        string.Equals(left.Hunk ?? string.Empty, right.Hunk ?? string.Empty, StringComparison.Ordinal);

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
