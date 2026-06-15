using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record IdeaProposal(
    string Title,
    string Rationale,
    string Scope,
    string Value,
    string Effort,
    string Risk);

internal sealed record IdeationPlan(
    IReadOnlyList<IdeaProposal> Ideas,
    IReadOnlyList<string> ValidationErrors)
{
    public bool IsValid => ValidationErrors.Count == 0;
}

internal static class IdeationProposalPlanner
{
    private static readonly Regex FencedJsonRegex = new(
        @"```(?:json)?\s*(\[[\s\S]*?\])\s*```",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A rationale must reference at least one evidence source or a quantitative data point.
    private static readonly string[] EvidenceMarkers =
    [
        "loop-health", "provenance", "escalation", "inbox", "backlog", "dogfood",
        "source survey", "failed", "rework", "dispatch", "retry", "metric"
    ];

    private static readonly Regex ContainsDigitRegex = new(@"\d",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildEvidenceContext(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Loop Health Metrics");
        try
        {
            var health = kernel.BuildLoopHealthReport(null);
            sb.AppendLine($"Goals: {health.GoalCount} total, {health.CompletedGoalCount} completed");
            sb.AppendLine($"Tasks: {health.TotalTaskCount} total, {health.TotalDispatchCount} dispatches");
            sb.AppendLine($"Dispatches per successful merge: {health.DispatchesPerSuccessfulMerge:F1}");
            sb.AppendLine($"Rework/retry rate: {health.ReworkRetryRate:P0}");
            sb.AppendLine($"False-completion catch rate: {health.FalseCompletionCatchRate:P0}");
            sb.AppendLine($"Operator prompts per goal: {health.OperatorPromptsPerGoal:F2}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(loop-health unavailable: {ex.Message})");
        }

        sb.AppendLine();
        sb.AppendLine("## BACKLOG.md (top 4000 chars)");
        var backlogPath = Path.Combine(workspace.ExecutionDirectory, "BACKLOG.md");
        if (File.Exists(backlogPath))
        {
            var text = File.ReadAllText(backlogPath);
            sb.AppendLine(text.Length > 4000 ? text[..4000] + "\n...(truncated)" : text);
        }
        else
        {
            sb.AppendLine("(not found)");
        }

        sb.AppendLine();
        sb.AppendLine("## DOGFOOD_LOG.md (recent 2000 chars)");
        var dogfoodPath = Path.Combine(workspace.ExecutionDirectory, "DOGFOOD_LOG.md");
        if (File.Exists(dogfoodPath))
        {
            var text = File.ReadAllText(dogfoodPath);
            sb.AppendLine(text.Length > 2000 ? text[^2000..] : text);
        }
        else
        {
            sb.AppendLine("(not found)");
        }

        return sb.ToString();
    }

    public static string BuildPrompt(string evidenceContext) => $$"""
        You are an ideation assistant for a software orchestration system. Based on the evidence context provided,
        propose a RANKED list of candidate improvements. Each idea must be grounded in the evidence.

        Output ONLY a fenced JSON array (```json ... ```) where each element has:
        - "title": short descriptive title
        - "rationale": reason grounded in evidence (MUST cite source, e.g. "loop-health shows 9% rework" or "3 escalations about X")
        - "scope": system area affected (e.g. "acceptance gate", "conductor loop", "worker dispatch")
        - "value": expected benefit
        - "effort": Low | Medium | High
        - "risk": potential downside

        Rules: rank highest-value/lowest-effort first; rationale MUST cite specific evidence; reject generic claims.

        EVIDENCE CONTEXT:
        {{evidenceContext}}

        Example:
        ```json
        [{"title":"Reduce rework rate","rationale":"loop-health shows 15% rework with 2+ retries per goal","scope":"acceptance gate","value":"Fewer wasted dispatches","effort":"Medium","risk":"May tighten gates too aggressively"}]
        ```
        """;

    public static IdeationPlan Parse(string workerOutput)
    {
        var match = FencedJsonRegex.Match(workerOutput);
        if (!match.Success)
            return new IdeationPlan([], ["Worker output did not contain a fenced JSON block."]);

        IReadOnlyList<IdeaProposal> ideas;
        try { ideas = ParseIdeas(match.Groups[1].Value); }
        catch (Exception ex)
        { return new IdeationPlan([], [$"Failed to parse JSON ideas: {ex.Message}"]); }

        var errors = Validate(ideas);
        return new IdeationPlan(ideas, errors);
    }

    public static string FormatBacklogEntry(IdeaProposal idea) =>
        $"""

        ## {idea.Title}

        {idea.Rationale} Scope: {idea.Scope}. Value: {idea.Value}. Effort: {idea.Effort}. Risk: {idea.Risk}.

        """;

    private static IReadOnlyList<IdeaProposal> ParseIdeas(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ideas = new List<IdeaProposal>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var title = element.GetProperty("title").GetString() ?? throw new JsonException("Idea 'title' is null.");
            var rationale = element.GetProperty("rationale").GetString() ?? throw new JsonException("Idea 'rationale' is null.");
            var scope = element.GetProperty("scope").GetString() ?? throw new JsonException("Idea 'scope' is null.");
            var value = element.GetProperty("value").GetString() ?? throw new JsonException("Idea 'value' is null.");
            var effort = element.GetProperty("effort").GetString() ?? throw new JsonException("Idea 'effort' is null.");
            var risk = element.GetProperty("risk").GetString() ?? throw new JsonException("Idea 'risk' is null.");
            ideas.Add(new IdeaProposal(title, rationale, scope, value, effort, risk));
        }
        return ideas;
    }

    private static IReadOnlyList<string> Validate(IReadOnlyList<IdeaProposal> ideas)
    {
        var errors = new List<string>();
        for (var i = 0; i < ideas.Count; i++)
        {
            var idea = ideas[i];
            if (string.IsNullOrWhiteSpace(idea.Title))
                errors.Add($"Idea {i + 1} has an empty title.");
            if (!HasEvidenceCitation(idea.Rationale))
                errors.Add($"Idea {i + 1} ('{idea.Title}') rationale has no evidence citation; it must cite a specific source or data point (e.g. loop-health, provenance, escalation count, backlog, dogfood).");
        }
        return errors;
    }

    internal static bool HasEvidenceCitation(string rationale)
    {
        if (string.IsNullOrWhiteSpace(rationale))
            return false;
        if (EvidenceMarkers.Any(m => rationale.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return true;
        return ContainsDigitRegex.IsMatch(rationale);
    }
}
