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
            var priorAtAnchor = previous.FirstOrDefault(prior => SameAnchor(prior.Location, newFinding.Location));
            if (priorAtAnchor is not null)
            {
                throw new ReviewFindingConvergenceException(
                    RecycledAnchorIdentityViolationCode,
                    CountOpen(previous),
                    CountOpen(nextRound.Findings),
                    $"Structural anchor '{newFinding.Location}' already belongs to stable_id '{priorAtAnchor.StableId}'; it cannot be recycled as '{newFinding.StableId}'.");
            }

            merged.Add(newFinding);
        }

        // Genuinely NEW stable_ids at fresh anchors may grow the open set: a reviewer discovering a real
        // defect late is doing its job, and rejecting the growth traps it — the structured report would
        // fail an open-set-increase check, a prose-only needs-work fails the no-open-findings rule, and
        // pass would be dishonest. (That trap cost four review rounds on 2026-07-25 before the increase
        // check was removed.) Re-litigation abuse stays blocked by the remaining guards: a still-open id
        // cannot move anchors, a resolved id cannot reopen without its anchor being touched, and a retired
        // anchor cannot be recycled under a new id.
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

    private static bool SameAnchor(ReviewFindingLocation left, ReviewFindingLocation right) =>
        string.Equals(left.File, right.File, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Region, right.Region, StringComparison.Ordinal) &&
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
