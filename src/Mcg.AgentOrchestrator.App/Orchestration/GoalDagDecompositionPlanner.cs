using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

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

    public static string BuildPrompt(string direction, bool sliceBatch) =>
        sliceBatch ? BuildSliceBatchPrompt(direction) : BuildPrompt(direction);

    private static string BuildSliceBatchPrompt(string direction) => $$"""
        Decompose the following direction into 2-4 file-disjoint Developer slice goals.
        Output ONLY a fenced JSON array (```json ... ```) where each element has:
        - "id": short unique label (e.g. "g1", "g2")
        - "objective": clear, actionable single-goal description containing this exact scope block:
          Target files/scopes:
          Scope confidence: precise
          Includes:
          - <repository-relative path>
        - "dependsOn": an empty array
        Rules: use 2-4 nodes, no dependency edges, and precise repository paths that do not overlap between nodes.
        Direction: {{direction}}
        Example:
        ```json
        [{"id":"g1","objective":"Implement model changes.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/Product/Model.cs","dependsOn":[]},{"id":"g2","objective":"Implement CLI changes.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/Product/Cli.cs","dependsOn":[]}]
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

    // Selects the best candidate from N samples: first valid plan by fewest nodes, then lowest sample index.
    // Returns the first candidate when none are valid (preserves ValidationErrors for the caller to surface).
    public static GoalDagPlan SelectBestOfN(IReadOnlyList<GoalDagPlan> candidates)
    {
        GoalDagPlan? best = null;
        var bestNodeCount = int.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.IsValid && candidate.Nodes.Count < bestNodeCount)
            {
                best = candidate;
                bestNodeCount = candidate.Nodes.Count;
            }
        }
        return best ?? candidates[0];
    }

    public static GoalDagPlan ValidateSliceBatch(GoalDagPlan plan)
    {
        var errors = plan.ValidationErrors.ToList();
        if (plan.Nodes.Count is < 2 or > 4)
        {
            errors.Add($"Slice-batch plan must contain 2-4 nodes; found {plan.Nodes.Count}.");
        }

        foreach (var duplicateId in plan.Nodes
                     .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"Slice-batch node id '{duplicateId}' is duplicated.");
        }

        foreach (var node in plan.Nodes)
        {
            foreach (var dependency in node.DependsOn)
            {
                errors.Add($"Slice-batch node '{node.Id}' has forbidden dependency edge '{node.Id} -> {dependency}'.");
            }
        }

        var scopes = new List<SliceBatchScope>();
        foreach (var node in plan.Nodes)
        {
            if (!ContainsDeclarationLine(node.Objective, BacklogIntakePlanner.TargetScopeHeadingLine))
            {
                errors.Add($"Slice-batch node '{node.Id}' is missing '{BacklogIntakePlanner.TargetScopeHeadingLine}'.");
                continue;
            }

            if (!ContainsDeclarationLine(node.Objective, BacklogIntakePlanner.PreciseScopeMarkerLine))
            {
                errors.Add($"Slice-batch node '{node.Id}' has non-precise scope: missing '{BacklogIntakePlanner.PreciseScopeMarkerLine}'.");
                continue;
            }

            if (!ContainsDeclarationLine(node.Objective, BacklogIntakePlanner.ScopeIncludesHeadingLine))
            {
                errors.Add($"Slice-batch node '{node.Id}' scope declaration is missing '{BacklogIntakePlanner.ScopeIncludesHeadingLine}'.");
                continue;
            }

            var stagedTask = new TaskSpec(TaskId.New(), "Implement the declared slice.", AgentRole.Developer);
            var stagedGoal = new Goal(GoalId.New(), node.Objective, [stagedTask]);
            var derivation = GoalFileScopeInference.ForScheduling(stagedGoal, stagedTask);
            if (derivation.Confidence != RepositoryScopeConfidence.Precise || derivation.Includes.Count == 0)
            {
                var reason = derivation.Warnings.Count == 0
                    ? "no included repository scope resolved"
                    : string.Join("; ", derivation.Warnings);
                errors.Add($"Slice-batch node '{node.Id}' scope is {derivation.Confidence}: {reason}.");
                continue;
            }

            scopes.Add(new SliceBatchScope(node.Id, derivation.Includes));
        }

        foreach (var collision in GoalScopeCollisionAdvisor.FindPairwiseOverlaps(scopes))
        {
            errors.Add(
                $"Slice-batch nodes '{collision.LeftNodeId}' ({collision.LeftPath}) and " +
                $"'{collision.RightNodeId}' ({collision.RightPath}) overlap ({collision.Kind}).");
        }

        return plan with { ValidationErrors = errors };
    }

    private static bool ContainsDeclarationLine(string objective, string declaration) =>
        objective.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Any(line => line.Trim().Equals(declaration, StringComparison.Ordinal));

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
