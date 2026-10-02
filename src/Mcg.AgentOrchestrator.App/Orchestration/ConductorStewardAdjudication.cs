using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorStewardAdjudication(
    string Kind,
    string Text,
    string? TargetTaskId = null,
    string? Cause = null,
    string? Reversibility = null,
    IReadOnlyList<string>? EvidenceReferences = null,
    string? Precedent = null,
    string? Instruction = null);

internal static class ConductorStewardAdjudicationParser
{
    internal static ConductorStewardAdjudication Parse(string output)
    {
        try
        {
            var trimmed = ConductorFencedJsonExtraction.LastFencedOrTrimmed(output);
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected object.");
            var kind = Read(root, "kind")?.Trim().ToLowerInvariant();
            if (kind is not ("route" or "ask-owner" or "no-action" or "reopen-regate" or "close" or "verify-manual"))
                throw new JsonException("Unknown adjudication kind.");
            var references = root.TryGetProperty("evidenceReferences", out var refs) && refs.ValueKind == JsonValueKind.Array
                ? refs.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
                : [];
            return new ConductorStewardAdjudication(kind,
                Read(root, "text") ?? Read(root, "question") ?? Read(root, "reason") ?? string.Empty,
                Read(root, "targetTaskId"), Read(root, "cause"), Read(root, "reversibility"),
                references, Read(root, "precedent"), Read(root, "instruction"));
        }
        catch (JsonException)
        {
            return new ConductorStewardAdjudication("no-action", "unparseable-output");
        }
    }

    private static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() : null;
}

internal static class ConductorStewardRoutePolicy
{
    internal static string? RejectionReason(
        ConductorStewardTrigger trigger,
        ConductorStewardAdjudication adjudication,
        Goal goal,
        string workingDirectory,
        AdjudicationEvidenceResolver evidenceResolver)
    {
        var caseDClose = trigger.Kind == ConductorStewardTriggerKind.DeveloperGateReopenNoCommit && adjudication.Kind == "close";
        if (!caseDClose && (adjudication.Kind != "route" ||
            trigger.Kind == ConductorStewardTriggerKind.DeveloperGateReopenNoCommit)) return "disallowed-action";
        if (!string.Equals(adjudication.TargetTaskId, trigger.TaskId, StringComparison.Ordinal))
            return "different-target-task";
        if (!caseDClose && adjudication.Cause is not (nameof(RetryCause.NewTestFinding) or nameof(RetryCause.ContractClarification)))
            return "disallowed-retry-cause";
        if ((!caseDClose || adjudication.Reversibility is not null) &&
            adjudication.Reversibility is not ("reversible" or "reversible-with-cost"))
            return "irreversible-or-unknown";
        if (caseDClose && string.IsNullOrWhiteSpace(adjudication.Text)) return "missing-diagnosis";
        if (!caseDClose && (string.IsNullOrWhiteSpace(adjudication.Text) || string.IsNullOrWhiteSpace(adjudication.Instruction)))
            return "missing-diagnosis-or-instruction";
        var payload = new AdjudicateOperatorIntentPayload(caseDClose ? "close" : "route", adjudication.Text,
            adjudication.EvidenceReferences ?? [], 0, workingDirectory);
        foreach (var reference in adjudication.EvidenceReferences ?? [])
            if (!evidenceResolver.TryResolve(reference, goal, payload, out _))
                return "evidence-reference-unresolved";
        return null;
    }
}
