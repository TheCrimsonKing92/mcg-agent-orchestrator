using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The semantic-acceptance question a judge answers: did the landed change actually DO what the
// objective asked, beyond merely passing the test suite? Advisory-only for now (see
// SemanticAcceptanceEvaluator) — recorded as a receipt, never blocks the merge yet.
internal sealed record SemanticAcceptanceVerdict(
    bool CriteriaMet,
    string Confidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> UnmetCriteria,
    IReadOnlyList<string> ValidationErrors)
{
    public bool IsValid => ValidationErrors.Count == 0;

    public static SemanticAcceptanceVerdict Invalid(string error) =>
        new(false, "unknown", [], [], [error]);
}

internal sealed record SemanticAcceptanceInputs(
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> ChangedFiles,
    string DiffExcerpt,
    string? TestSummary,
    IReadOnlyList<(string File, string Diff)>? PerFileDiffs = null);

internal static class SemanticAcceptancePlanner
{
    // The judge must return a single fenced JSON OBJECT (not an array).
    private static readonly Regex FencedJsonRegex = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildEvidenceContext(SemanticAcceptanceInputs inputs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Objective");
        sb.AppendLine(inputs.Objective);
        sb.AppendLine();

        sb.AppendLine("## Stated acceptance criteria");
        if (inputs.AcceptanceCriteria.Count == 0)
        {
            sb.AppendLine("(none recorded; judge against the objective itself)");
        }
        else
        {
            foreach (var criterion in inputs.AcceptanceCriteria)
            {
                sb.AppendLine($"- {criterion}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Changed files");
        if (inputs.ChangedFiles.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var file in inputs.ChangedFiles)
            {
                sb.AppendLine($"- {file}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Test/verification summary");
        sb.AppendLine(string.IsNullOrWhiteSpace(inputs.TestSummary) ? "(none)" : inputs.TestSummary);

        sb.AppendLine();
        sb.AppendLine("## Unified diff (may be truncated)");
        sb.AppendLine(string.IsNullOrWhiteSpace(inputs.DiffExcerpt) ? "(empty)" : inputs.DiffExcerpt);

        return sb.ToString();
    }

    public static string BuildPrompt(string evidenceContext) => $$"""
        You are a STRICT software acceptance reviewer. You are given an objective, its stated
        acceptance criteria, the list of changed files, a test/verification summary, and a unified
        diff. Decide whether the change ACTUALLY accomplishes what the objective asked — not merely
        whether tests pass. A change that compiles and passes tests but does not implement the
        stated objective has NOT met the criteria.

        Output ONLY a fenced JSON object (```json ... ```) with these fields:
        - "criteria_met": boolean — true only if the diff plausibly satisfies the objective/criteria
        - "confidence": "high" | "medium" | "low"
        - "reasons": array of short strings, each citing concrete evidence from the diff or summary
        - "unmet_criteria": array of short strings naming any criteria the diff does NOT satisfy (empty if all met)

        Be specific and evidence-grounded; do not restate the objective as a reason.

        EVIDENCE:
        {{evidenceContext}}

        Example:
        ```json
        {"criteria_met": true, "confidence": "high", "reasons": ["adds GetDiffExcerpt and wires it into the judge", "tests cover parse + evaluator"], "unmet_criteria": []}
        ```
        """;

    public static SemanticAcceptanceVerdict Parse(string workerOutput)
    {
        if (string.IsNullOrWhiteSpace(workerOutput))
        {
            return SemanticAcceptanceVerdict.Invalid("Judge produced no output.");
        }

        var match = FencedJsonRegex.Match(workerOutput);
        if (!match.Success)
        {
            return SemanticAcceptanceVerdict.Invalid("Judge output did not contain a fenced JSON object.");
        }

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;
            if (!root.TryGetProperty("criteria_met", out var metElement) ||
                (metElement.ValueKind != JsonValueKind.True && metElement.ValueKind != JsonValueKind.False))
            {
                return SemanticAcceptanceVerdict.Invalid("Judge output is missing a boolean 'criteria_met'.");
            }

            var criteriaMet = metElement.GetBoolean();
            var confidence = root.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.String
                ? conf.GetString()!
                : "unknown";
            var reasons = ReadStringArray(root, "reasons");
            var unmet = ReadStringArray(root, "unmet_criteria");

            return new SemanticAcceptanceVerdict(criteriaMet, confidence, reasons, unmet, []);
        }
        catch (JsonException ex)
        {
            return SemanticAcceptanceVerdict.Invalid($"Failed to parse judge JSON: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text);
                }
            }
        }

        return values;
    }
}
