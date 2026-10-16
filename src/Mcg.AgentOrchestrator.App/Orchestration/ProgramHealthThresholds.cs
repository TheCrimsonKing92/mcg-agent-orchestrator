using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ProgramHealthThresholds(TimeSpan NoLanding, TimeSpan HeldReceipt)
{
    internal ProgramHealthThresholds() : this(TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(30)) { }

    internal static ProgramHealthThresholds Read(string? policyJson)
    {
        var defaults = new ProgramHealthThresholds();
        if (string.IsNullOrWhiteSpace(policyJson)) return defaults;
        try
        {
            using var document = JsonDocument.Parse(policyJson,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("programHealth", out var health) ||
                health.ValueKind != JsonValueKind.Object) return defaults;
            TimeSpan ReadMinutes(string key, TimeSpan fallback) =>
                health.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var minutes) && minutes is >= 1 and <= 10080
                    ? TimeSpan.FromMinutes(minutes) : fallback;
            return new(ReadMinutes("noLandingMinutes", defaults.NoLanding),
                ReadMinutes("heldReceiptMinutes", defaults.HeldReceipt));
        }
        catch (JsonException) { return defaults; }
    }
}
