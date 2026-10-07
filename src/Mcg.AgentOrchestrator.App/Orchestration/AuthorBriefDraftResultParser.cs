using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AuthorBriefDraftResult(string Kind, string? Markdown, string? Reason,
    IReadOnlyList<string> EvidenceReferences);

internal static class AuthorBriefDraftResultParser
{
    internal static AuthorBriefDraftResult? Parse(string output)
    {
        // Parse the entire response so a second object or trailing narrative cannot be silently ignored.
        try
        {
            return FromDocument(output);
        }
        catch (JsonException)
        {
            // A brace-free prefix makes the first opening brace the unambiguous object start.
            var start = output.IndexOf('{');
            if (start < 0 || output.AsSpan(0, start).IndexOfAny('{', '}') >= 0) return null;
            try
            {
                return FromDocument(output[start..]);
            }
            catch (JsonException) { return null; }
        }
    }

    private static AuthorBriefDraftResult? FromDocument(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) ||
            kind.ValueKind != JsonValueKind.String) return null;
        if (kind.GetString() == "draft" && root.TryGetProperty("markdown", out var markdown) &&
            markdown.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(markdown.GetString()))
            return new("draft", markdown.GetString(), null, []);
        if (kind.GetString() != "stale" || !root.TryGetProperty("reason", out var reason) ||
            reason.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reason.GetString()) ||
            !root.TryGetProperty("evidenceReferences", out var evidence) ||
            evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() == 0 ||
            evidence.EnumerateArray().Any(reference => reference.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(reference.GetString()))) return null;
        return new("stale", null, reason.GetString(), evidence.EnumerateArray().Select(reference => reference.GetString()!).ToArray());
    }
}
