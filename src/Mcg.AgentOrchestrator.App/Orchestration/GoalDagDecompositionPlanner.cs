using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalDagNode(string Id, string Objective, IReadOnlyList<string> DependsOn);

internal sealed record GoalDagPlan(
    string Direction,
    IReadOnlyList<GoalDagNode> Nodes,
    IReadOnlyList<string> ValidationErrors)
{
    public bool IsValid => ValidationErrors.Count == 0;
}

internal static class GoalDagDecompositionPlanner
{
    private static readonly Regex FencedJsonRegex = new(
        @"```(?:json)?\s*(\[[\s\S]*?\])\s*```",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildPrompt(string direction) => $$"""
        Decompose the following direction into an ordered set of independent work goals.
        Output ONLY a fenced JSON array (```json ... ```) where each element has:
        - "id": short unique label (e.g. "g1", "g2")
        - "objective": clear, actionable single-goal description
        - "dependsOn": array of earlier node ids (empty array if independent)
        Rules: no cycles, no self-references, all dependsOn ids must be in this list.
        Direction: {{direction}}
        Example:
        ```json
        [{"id":"g1","objective":"Set up data model","dependsOn":[]},{"id":"g2","objective":"Implement service","dependsOn":["g1"]}]
        ```
        """;

    public static GoalDagPlan Parse(string direction, string workerOutput)
    {
        var match = FencedJsonRegex.Match(workerOutput);
        if (!match.Success)
            return new GoalDagPlan(direction, [], ["Worker output did not contain a fenced JSON block."]);

        IReadOnlyList<GoalDagNode> nodes;
        try { nodes = ParseNodes(match.Groups[1].Value); }
        catch (Exception ex)
        { return new GoalDagPlan(direction, [], [$"Failed to parse JSON nodes: {ex.Message}"]); }

        var errors = Validate(nodes);
        return new GoalDagPlan(direction, nodes, errors);
    }

    private static IReadOnlyList<GoalDagNode> ParseNodes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var nodes = new List<GoalDagNode>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var id = element.GetProperty("id").GetString() ?? throw new JsonException("Node 'id' is null.");
            var objective = element.GetProperty("objective").GetString() ?? throw new JsonException("Node 'objective' is null.");
            var dependsOn = new List<string>();
            if (element.TryGetProperty("dependsOn", out var depsEl))
                foreach (var dep in depsEl.EnumerateArray())
                    dependsOn.Add(dep.GetString() ?? throw new JsonException("dependsOn entry is null."));
            nodes.Add(new GoalDagNode(id, objective, dependsOn));
        }
        return nodes;
    }

    private static IReadOnlyList<string> Validate(IReadOnlyList<GoalDagNode> nodes)
    {
        var errors = new List<string>();
        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id)) { errors.Add("A node has an empty id."); continue; }
            if (node.DependsOn.Contains(node.Id, StringComparer.OrdinalIgnoreCase))
                errors.Add($"Node '{node.Id}' depends on itself.");
            foreach (var dep in node.DependsOn)
                if (!ids.Contains(dep))
                    errors.Add($"Node '{node.Id}' references unknown node '{dep}'.");
        }
        if (errors.Count == 0 && HasCycle(nodes))
            errors.Add("The dependency graph contains a cycle.");
        return errors;
    }

    private static bool HasCycle(IReadOnlyList<GoalDagNode> nodes)
    {
        var adjacency = nodes.ToDictionary(
            n => n.Id,
            n => (IReadOnlyList<string>)n.DependsOn,
            StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Dfs(string nodeId)
        {
            if (inStack.Contains(nodeId)) return true;
            if (visited.Contains(nodeId)) return false;
            visited.Add(nodeId);
            inStack.Add(nodeId);
            if (adjacency.TryGetValue(nodeId, out var deps))
                foreach (var dep in deps)
                    if (Dfs(dep)) return true;
            inStack.Remove(nodeId);
            return false;
        }

        return nodes.Any(node => Dfs(node.Id));
    }
}
