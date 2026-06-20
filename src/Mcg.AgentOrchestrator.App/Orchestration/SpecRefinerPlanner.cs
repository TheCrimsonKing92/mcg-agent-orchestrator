using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum SpecRefinerConfidence { Low, Med, High }
internal enum SpecBlastRadius { High, Low }
internal enum SpecForkDisposition { Decide, Ask }

internal sealed record SpecRefinementFork(
    string Kind,
    string RefinerConfidence,
    string BlastRadius,
    string Question,
    string Choice,
    string Rationale);

internal sealed record SpecRefinementOutput(
    string BehavioralContract,
    IReadOnlyList<string> AcceptanceCriteria,
    VerificationClass VerificationClass,
    IReadOnlyList<RefinedSpecDecision> Decisions,
    IReadOnlyList<SpecRefinementFork> Forks,
    IReadOnlyList<string> ValidationErrors)
{
    public bool IsValid => ValidationErrors.Count == 0;

    public static SpecRefinementOutput Invalid(string error) =>
        new(string.Empty, [], VerificationClass.TestVerifiable, [], [], [error]);
}

internal static class SpecRefinerPlanner
{
    private static readonly Regex FencedJsonRegex = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareJsonRegex = new(
        @"\{[\s\S]*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildPrompt(string objective) => $$"""
        You are a SPECIFICATION REFINER. Convert the raw objective into a structured spec.

        For each ambiguity that could lead to materially different implementations, classify it on three axes:
        - kind: one of external-contract | observable-behavior | ownership-lifecycle | reversibility | other
        - refinerConfidence: low | med | high  (your confidence in the right answer WITHOUT operator input)
        - blastRadius: high | low  (how hard it is to change later if you pick wrong)

        RULE: if refinerConfidence == low AND blastRadius == high, set disposition to "ask" (leave choice empty).
        Otherwise decide and record choice + rationale.

        OUTPUT ONLY a fenced JSON object (```json ... ```) with these fields:
        - "behavioralContract": string — one paragraph: WHAT the system does, observable from the outside
        - "acceptanceCriteria": string array — concrete, testable outcomes
        - "verificationClass": "TestVerifiable" | "RealWorldDependent"
        - "decisions": array of {question, choice, rationale} — forks you resolved yourself
        - "forks": array of {kind, refinerConfidence, blastRadius, question, choice, rationale} — ALL forks

        For ask forks: provide empty choice, explain in rationale what information is needed.
        Include decided forks in BOTH "decisions" and "forks".

        OBJECTIVE:
        {{objective}}

        Example:
        ```json
        {
          "behavioralContract": "The API exposes goal status as a JSON object over HTTP.",
          "acceptanceCriteria": ["GET /goals/{id} returns 200 with status field", "404 for unknown id"],
          "verificationClass": "TestVerifiable",
          "decisions": [{"question": "HTTP method?", "choice": "GET", "rationale": "Read-only; idempotent."}],
          "forks": [{"kind": "observable-behavior", "refinerConfidence": "high", "blastRadius": "low", "question": "HTTP method?", "choice": "GET", "rationale": "Read-only; idempotent."}]
        }
        ```
        """;

    public static SpecRefinementOutput Parse(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
            return SpecRefinementOutput.Invalid("Spec refiner produced no output.");

        var match = FencedJsonRegex.Match(modelOutput);
        string jsonCandidate;
        if (match.Success)
        {
            jsonCandidate = match.Groups[1].Value;
        }
        else
        {
            var bare = BareJsonRegex.Match(modelOutput);
            if (!bare.Success)
                return SpecRefinementOutput.Invalid("Spec refiner output did not contain a JSON object.");
            jsonCandidate = bare.Value;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonCandidate);
            var root = doc.RootElement;

            var contract = ReadString(root, "behavioralContract");
            if (string.IsNullOrWhiteSpace(contract))
                return SpecRefinementOutput.Invalid("Missing 'behavioralContract' field.");

            var criteria = ReadStringArray(root, "acceptanceCriteria");
            var vc = string.Equals(ReadString(root, "verificationClass"), "RealWorldDependent", StringComparison.Ordinal)
                ? VerificationClass.RealWorldDependent
                : VerificationClass.TestVerifiable;

            var decisions = ReadDecisions(root);
            var forks = ReadForks(root);

            return new SpecRefinementOutput(contract, criteria, vc, decisions, forks, []);
        }
        catch (JsonException ex)
        {
            return SpecRefinementOutput.Invalid($"Failed to parse spec refiner JSON: {ex.Message}");
        }
    }

    public static SpecForkDisposition ClassifyFork(SpecRefinementFork fork)
        => ClassifyFork(fork, ConductorAutonomyPolicy.Permissive);

    public static SpecForkDisposition ClassifyFork(SpecRefinementFork fork, ConductorAutonomyPolicy policy)
    {
        var confidence = ParseConfidence(fork.RefinerConfidence);
        var blast = ParseBlastRadius(fork.BlastRadius);
        var kind = fork.Kind.Trim().ToLowerInvariant();

        if (IsFullAuto(policy))
        {
            return confidence == SpecRefinerConfidence.Low
                && blast == SpecBlastRadius.High
                && IsAutonomousStopKind(kind)
                    ? SpecForkDisposition.Ask
                    : SpecForkDisposition.Decide;
        }

        if (string.Equals(policy.Name, ConductorAutonomyPolicy.Conservative.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(policy.Name, ConductorAutonomyPolicy.Manual.Name, StringComparison.OrdinalIgnoreCase))
        {
            return confidence == SpecRefinerConfidence.High && blast == SpecBlastRadius.Low
                ? SpecForkDisposition.Decide
                : SpecForkDisposition.Ask;
        }

        return (confidence == SpecRefinerConfidence.Low && blast == SpecBlastRadius.High)
            || (blast == SpecBlastRadius.High && IsBalancedHighStakesKind(kind))
                ? SpecForkDisposition.Ask
                : SpecForkDisposition.Decide;
    }

    private static bool IsFullAuto(ConductorAutonomyPolicy policy) =>
        policy.Name.Equals("full-auto", StringComparison.OrdinalIgnoreCase) ||
        policy.Name.Equals("fullauto", StringComparison.OrdinalIgnoreCase);

    private static bool IsBalancedHighStakesKind(string kind) =>
        kind is "external-contract" or "ownership-lifecycle" or "reversibility" or "architecture" or "scope";

    private static bool IsAutonomousStopKind(string kind) =>
        kind is "reversibility" or "security" or "security-sensitive" or "blocking" or "genuinely-blocking";

    private static SpecRefinerConfidence ParseConfidence(string value) => value.ToLowerInvariant() switch
    {
        "low" => SpecRefinerConfidence.Low,
        "high" => SpecRefinerConfidence.High,
        _ => SpecRefinerConfidence.Med
    };

    private static SpecBlastRadius ParseBlastRadius(string value) =>
        string.Equals(value, "high", StringComparison.OrdinalIgnoreCase)
            ? SpecBlastRadius.High
            : SpecBlastRadius.Low;

    private static string ReadString(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!string.IsNullOrWhiteSpace(s))
                result.Add(s!);
        }
        return result;
    }

    private static IReadOnlyList<RefinedSpecDecision> ReadDecisions(JsonElement root)
    {
        if (!root.TryGetProperty("decisions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<RefinedSpecDecision>();
        foreach (var item in arr.EnumerateArray())
        {
            var q = ReadString(item, "question");
            var c = ReadString(item, "choice");
            var r = ReadString(item, "rationale");
            if (!string.IsNullOrWhiteSpace(q))
                result.Add(new RefinedSpecDecision(q, c, r));
        }
        return result;
    }

    private static IReadOnlyList<SpecRefinementFork> ReadForks(JsonElement root)
    {
        if (!root.TryGetProperty("forks", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<SpecRefinementFork>();
        foreach (var item in arr.EnumerateArray())
        {
            var q = ReadString(item, "question");
            if (string.IsNullOrWhiteSpace(q))
                continue;
            result.Add(new SpecRefinementFork(
                ReadString(item, "kind"),
                ReadString(item, "refinerConfidence"),
                ReadString(item, "blastRadius"),
                q,
                ReadString(item, "choice"),
                ReadString(item, "rationale")));
        }
        return result;
    }
}
