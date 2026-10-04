using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum PanelJudgeOutcome { Valid, InvalidOutput, EmptyOutput, InvocationFailed, TimedOut, Skipped }
internal enum PanelCaseTerminal { Completed, Superseded, SuspendedSkip, BudgetSkip }
internal enum PanelJudgeHealthState { Active, Suspended }

internal sealed record PanelJudgeResult(string Judge, PanelJudgeOutcome Outcome, int? ExitCode,
    string RawStdout, string RawStderr, string? Answer = null, string? Reason = null,
    string? UsageJson = null, string? ModelAlias = null);

internal static class PanelV0Contract
{
    // Recommendations are data in the shadow ledger, never executable commands.
    internal static readonly IReadOnlyList<string> ActionKinds = Array.AsReadOnly(new[]
        { "no-action", "request-evidence", "retry", "repair", "ask-owner" });
    internal const string JsonSchema = """
        {"type":"object","additionalProperties":false,"required":["schema","case_id","assessment","next_action","discriminating_observation","missing_evidence"],"properties":{"schema":{"const":"panel-v0"},"case_id":{"type":"string"},"assessment":{"enum":["supported","contradicted","insufficient-evidence"]},"next_action":{"type":"object","additionalProperties":false,"required":["kind","owner","detail"],"properties":{"kind":{"enum":["no-action","request-evidence","retry","repair","ask-owner"]},"owner":{"type":"string","minLength":1},"detail":{"type":"string","minLength":1}}},"discriminating_observation":{"type":"string"},"missing_evidence":{"type":"array","items":{"type":"string"}}}}
        """;

    internal static string Prompt(PanelCase item) =>
        $"Judge only the supplied decision-time evidence. Do not inspect files or use tools. " +
        $"Return exactly one panel-v0 JSON object, without fences or prose. case_id={item.Id}. " +
        $"Schema: {JsonSchema}\nPacket:\n{item.Key.Packet}";

    internal static PanelJudgeOutcome Validate(string? output, string caseId)
    {
        if (string.IsNullOrWhiteSpace(output)) return PanelJudgeOutcome.EmptyOutput;
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (!ExactProperties(root, "schema", "case_id", "assessment", "next_action",
                    "discriminating_observation", "missing_evidence") ||
                Text(root, "schema") != "panel-v0" || Text(root, "case_id") != caseId ||
                Text(root, "assessment") is not ("supported" or "contradicted" or "insufficient-evidence"))
                return PanelJudgeOutcome.InvalidOutput;
            var action = root.GetProperty("next_action");
            if (!ExactProperties(action, "kind", "owner", "detail") ||
                !ActionKinds.Contains(Text(action, "kind"), StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(Text(action, "owner")) || string.IsNullOrWhiteSpace(Text(action, "detail")) ||
                Text(root, "discriminating_observation") is null)
                return PanelJudgeOutcome.InvalidOutput;
            var missing = root.GetProperty("missing_evidence");
            return missing.ValueKind == JsonValueKind.Array &&
                   missing.EnumerateArray().All(entry => entry.ValueKind == JsonValueKind.String)
                ? PanelJudgeOutcome.Valid : PanelJudgeOutcome.InvalidOutput;
        }
        catch (JsonException) { return PanelJudgeOutcome.InvalidOutput; }
    }

    private static string? Text(JsonElement element, string key) =>
        element.GetProperty(key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool ExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == names.Length && actual.Distinct(StringComparer.Ordinal).Count() == names.Length &&
               names.All(name => actual.Contains(name, StringComparer.Ordinal));
    }

    internal static string Token(PanelJudgeOutcome value) => value switch
    {
        PanelJudgeOutcome.Valid => "valid", PanelJudgeOutcome.InvalidOutput => "invalid-output",
        PanelJudgeOutcome.EmptyOutput => "empty-output", PanelJudgeOutcome.InvocationFailed => "invocation-failed",
        PanelJudgeOutcome.TimedOut => "timed-out", PanelJudgeOutcome.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    internal static string Token(PanelCaseTerminal value) => value switch
    {
        PanelCaseTerminal.Completed => "completed", PanelCaseTerminal.Superseded => "superseded",
        PanelCaseTerminal.SuspendedSkip => "suspended-skip", PanelCaseTerminal.BudgetSkip => "budget-skip",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
