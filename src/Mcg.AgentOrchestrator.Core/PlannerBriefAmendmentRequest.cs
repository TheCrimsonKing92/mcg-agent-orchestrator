using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

internal sealed record PlannerBriefAmendmentRequest(
    int CriterionIndex, string Defect, string Evidence, string ProposedText, string? FallbackText)
{
    private const string Prefix = "PLANNER_BRIEF_AMENDMENT:";

    internal static HumanInputDirectiveParseResult? TryParse(IReadOnlyList<string> lines)
    {
        var amendments = lines.Select(line => line.Trim())
            .Where(line => line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (amendments.Length == 0) return null;
        if (amendments.Length > 1) return Malformed("only one amendment line is allowed per round");
        try
        {
            using var document = JsonDocument.Parse(amendments[0][Prefix.Length..].Trim());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Malformed("payload must be a JSON object");
            if (!root.TryGetProperty("criterion_index", out var criterion) ||
                criterion.ValueKind != JsonValueKind.Number || !criterion.TryGetInt32(out var index) || index <= 0)
                return Malformed("criterion_index must be a positive 1-based integer");
            var defect = ReadRequiredString(root, "defect");
            if (defect is null) return Malformed("defect must be a non-empty string");
            var evidence = ReadRequiredString(root, "evidence");
            if (evidence is null) return Malformed("evidence must be a non-empty string in path:line form");
            var colon = evidence.LastIndexOf(':');
            if (colon <= 0 || string.IsNullOrWhiteSpace(evidence[..colon]) ||
                !int.TryParse(evidence.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line <= 0)
                return Malformed("evidence must contain a non-empty path followed by a positive line number in path:line form");
            var proposed = ReadRequiredString(root, "proposed_text");
            if (proposed is null) return Malformed("proposed_text must be a non-empty string");
            string? fallback = null;
            if (root.TryGetProperty("fallback_text", out var fallbackProperty) && fallbackProperty.ValueKind != JsonValueKind.Null)
            {
                if (fallbackProperty.ValueKind != JsonValueKind.String)
                    return Malformed("fallback_text must be a string or null");
                fallback = fallbackProperty.GetString()?.Trim();
            }
            return new PlannerBriefAmendmentRequest(index, defect, evidence, proposed, fallback).ToDirective();
        }
        catch (JsonException)
        {
            return Malformed("payload is not valid JSON");
        }
    }

    internal static HumanInputDirectiveParseResult Resolve(HumanInputDirectiveParseResult amendment, string? question) =>
        question is null ? amendment : Malformed("substantive HUMAN_INPUT conflicts with the amendment clarification");

    private HumanInputDirectiveParseResult ToDirective()
    {
        var question = string.Join(Environment.NewLine,
            $"Planner brief amendment requested for criterion {CriterionIndex.ToString(CultureInfo.InvariantCulture)}.",
            $"Defect: {Defect}",
            $"Evidence: {Evidence}",
            $"Proposed text: {ProposedText}",
            $"Fallback: {(string.IsNullOrWhiteSpace(FallbackText) ? "No fallback supplied." : FallbackText)}",
            "What is the ruling on this criterion amendment?");
        var fingerprint = HumanInputRequest.BuildQuestionFingerprint(
            $"planner-brief-amendment\ncriterion:{CriterionIndex.ToString(CultureInfo.InvariantCulture)}\ndefect:{Defect}");
        return new(new HumanInputDirective(question, fingerprint,
            fingerprint, HumanWaitKind.SpecClarification), null);
    }

    private static string? ReadRequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()!.Trim() : null;

    private static HumanInputDirectiveParseResult Malformed(string diagnostic) =>
        new(null, $"Malformed PLANNER_BRIEF_AMENDMENT: {diagnostic}.");
}
