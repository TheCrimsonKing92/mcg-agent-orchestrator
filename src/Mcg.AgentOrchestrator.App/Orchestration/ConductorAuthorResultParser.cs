using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAuthorResult(
    string Kind, string? Text, IReadOnlyList<string>? EvidenceReferences,
    string? Precedent, string? Question, string? Recommendation);

internal static class ConductorAuthorResultParser
{
    internal static ConductorAuthorResult? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var fenced = ConductorFencedJsonExtraction.FencedObjects(output);
        if (fenced.Count > 1) return null;
        var json = fenced.Count == 1 ? fenced[0] : output.Trim();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement) ||
                kindElement.ValueKind != JsonValueKind.String) return null;
            var kind = kindElement.GetString();
            if (kind == "answer")
            {
                if (!TryText(root, "text", out var text) || string.IsNullOrWhiteSpace(text)) return null;
                IReadOnlyList<string>? references = null;
                if (root.TryGetProperty("evidenceReferences", out var refs) && refs.ValueKind == JsonValueKind.Array)
                {
                    if (refs.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) return null;
                    references = refs.EnumerateArray().Select(value => value.GetString()!).ToArray();
                }
                TryText(root, "precedent", out var precedent);
                return new ConductorAuthorResult(kind, text, references, precedent, null, null);
            }
            if (kind == "ask-owner" && TryText(root, "question", out var question) &&
                TryText(root, "recommendation", out var recommendation) &&
                !string.IsNullOrWhiteSpace(question) && !string.IsNullOrWhiteSpace(recommendation))
                return new ConductorAuthorResult(kind, null, null, null, question, recommendation);
        }
        catch (JsonException) { }
        return null;
    }

    private static bool TryText(JsonElement root, string property, out string? text)
    {
        text = null;
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString();
        return true;
    }
}
