using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalDependencyPlan(
    IReadOnlyList<GoalPlanNode> Nodes,
    IReadOnlyList<GoalPlanEdge> Edges,
    CompiledGoalGraph CompiledGraph,
    ParallelExecutionPlan ParallelPlan);

internal sealed record GoalPlanNode(
    string Id,
    string Heading,
    BacklogIntakeItem Intake,
    IReadOnlyList<string> DependsOn,
    string ReadyObjective);

internal sealed record GoalPlanEdge(
    string FromId,
    string ToId,
    string Reason);

internal sealed record CompiledGoalGraph(
    string GraphId,
    IReadOnlyList<CompiledGoalNode> Nodes,
    IReadOnlyList<CompiledGoalEdge> Edges,
    IReadOnlyList<CompiledGoalValidationFinding> Findings,
    bool IsRunnable);

internal sealed record CompiledGoalNode(
    string Id,
    string Heading,
    IReadOnlyList<string> FileScopes,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> VerificationContracts,
    string RollbackBoundary,
    int? ParallelBatch,
    ParallelExecutionDisposition ParallelDisposition,
    bool CanCreateGoal);

internal sealed record CompiledGoalEdge(
    string FromId,
    string ToId,
    string Reason);

internal sealed record CompiledGoalValidationFinding(
    string Severity,
    string NodeId,
    string Message);

internal static class GoalDependencyPlanner
{
    public static GoalDependencyPlan Build(BacklogIntakePlan intake)
    {
        var nodes = intake.Items
            .Select((item, index) => BuildNode(item, index + 1))
            .ToArray();
        var edges = BuildEdges(nodes);
        var nodesWithDependencies = nodes
            .Select(node =>
            {
                var dependencies = edges
                    .Where(edge => edge.ToId == node.Id)
                    .Select(edge => edge.FromId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return node with
                {
                    DependsOn = dependencies,
                    ReadyObjective = BuildReadyObjective(node.Intake, dependencies, edges)
                };
            })
            .ToArray();
        var parallel = ParallelExecutionPlanner.Build(nodesWithDependencies.Select(BuildIntent).ToArray());
        var compiledGraph = CompileGraph(nodesWithDependencies, edges, parallel);
        return new GoalDependencyPlan(nodesWithDependencies, edges, compiledGraph, parallel);
    }

    private static GoalPlanNode BuildNode(BacklogIntakeItem item, int number)
    {
        var id = $"g{number}";
        return new GoalPlanNode(id, item.Heading, item, [], item.SuggestedObjective);
    }

    private static GoalPlanEdge[] BuildEdges(IReadOnlyList<GoalPlanNode> nodes)
    {
        var edges = new List<GoalPlanEdge>();
        foreach (var target in nodes)
        {
            var dependencyText = string.Join(' ', target.Intake.Dependencies);
            foreach (var source in nodes)
            {
                if (source.Id == target.Id)
                {
                    continue;
                }

                if (DependsOn(target, source, dependencyText, out var reason))
                {
                    edges.Add(new GoalPlanEdge(source.Id, target.Id, reason));
                }
            }
        }

        return edges
            .DistinctBy(edge => $"{edge.FromId}->{edge.ToId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool DependsOn(GoalPlanNode target, GoalPlanNode source, string dependencyText, out string reason)
    {
        var targetText = $"{target.Heading}\n{target.Intake.Body}";
        var sourceText = $"{source.Heading}\n{source.Intake.Body}";
        if (Mentions(dependencyText, "parallel") && Mentions(sourceText, "parallel"))
        {
            reason = "declared dependency on parallel safety";
            return true;
        }

        if (Mentions(dependencyText, "recovery") && Mentions(sourceText, "recovery"))
        {
            reason = "declared dependency on recovery gates";
            return true;
        }

        if (Mentions(targetText, "unattended", "supervisor") && Mentions(sourceText, "autonomy", "policy"))
        {
            reason = "supervisor should obey autonomy policy presets";
            return true;
        }

        if (Mentions(targetText, "dashboard", "subscription consumers") && Mentions(sourceText, "operator inbox"))
        {
            reason = "dashboard subscription consumers need operator inbox concepts";
            return true;
        }

        if (Mentions(targetText, "parallel", "concurrent") && Mentions(sourceText, "build/test broker", "build environment"))
        {
            reason = "parallel execution requires deterministic build isolation";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static ParallelExecutionIntent BuildIntent(GoalPlanNode node)
    {
        var text = $"{node.Heading}\n{node.Intake.Body}";
        var mutatesLifecycle = Mentions(text, "workspace", "lifecycle", "rollback", "abandon", "cleanup");
        var runsAcceptance = Mentions(text, "acceptance");
        return new ParallelExecutionIntent(
            node.Id,
            GoalKey: node.Id,
            TargetPaths: node.Intake.TargetFiles,
            RequiredResources: BuildResources(node),
            WritesGoalState: mutatesLifecycle || runsAcceptance,
            MutatesWorkspaceLifecycle: mutatesLifecycle,
            RunsAcceptance: runsAcceptance,
            RequiresOperatorApproval: node.Intake.RiskLabels.Contains("subscription-cost", StringComparer.OrdinalIgnoreCase),
            ProviderKey: node.Intake.RiskLabels.Contains("subscription-cost", StringComparer.OrdinalIgnoreCase) ? "subscription" : null,
            DependsOn: node.DependsOn);
    }

    private static List<string> BuildResources(GoalPlanNode node)
    {
        var resources = new List<string>();
        if (node.Intake.RiskLabels.Contains("concurrency", StringComparer.OrdinalIgnoreCase))
        {
            resources.Add("parallel-safety");
        }

        if (node.Intake.RiskLabels.Contains("state-and-worktree-mutation", StringComparer.OrdinalIgnoreCase))
        {
            resources.Add("worktree-lifecycle");
        }

        return resources;
    }

    private static CompiledGoalGraph CompileGraph(
        IReadOnlyList<GoalPlanNode> nodes,
        IReadOnlyList<GoalPlanEdge> edges,
        ParallelExecutionPlan parallel)
    {
        var decisions = parallel.Decisions.ToDictionary(
            decision => decision.IntentId,
            StringComparer.OrdinalIgnoreCase);
        var compiledNodes = nodes
            .Select(node =>
            {
                decisions.TryGetValue(node.Id, out var decision);
                var verificationContracts = BuildVerificationContracts(node);
                return new CompiledGoalNode(
                    node.Id,
                    node.Heading,
                    node.Intake.TargetFiles,
                    BuildRequiredCapabilities(node),
                    verificationContracts,
                    BuildRollbackBoundary(node),
                    decision?.BatchNumber,
                    decision?.Disposition ?? ParallelExecutionDisposition.RequiresOperatorApproval,
                    CanCreateGoal(node, verificationContracts, decision));
            })
            .ToArray();
        var findings = BuildValidationFindings(nodes, compiledNodes, edges, parallel).ToArray();
        return new CompiledGoalGraph(
            BuildGraphId(compiledNodes, edges),
            compiledNodes,
            edges.Select(edge => new CompiledGoalEdge(edge.FromId, edge.ToId, edge.Reason)).ToArray(),
            findings,
            !findings.Any(finding => finding.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] BuildRequiredCapabilities(GoalPlanNode node)
    {
        var capabilities = new List<string>();
        var text = $"{node.Heading}\n{node.Intake.Body}";
        if (node.Intake.Roles.Contains(AgentRole.Developer) || node.Intake.Roles.Contains(AgentRole.Tester))
        {
            capabilities.Add("workspace-write");
        }

        if (node.Intake.RiskLabels.Contains("subscription-cost", StringComparer.OrdinalIgnoreCase))
        {
            capabilities.Add("subscription-budget");
        }

        if (node.Intake.RiskLabels.Contains("concurrency", StringComparer.OrdinalIgnoreCase) ||
            Mentions(text, "build", "build/test", "dotnet test", "CS2012"))
        {
            capabilities.Add("build-test-broker");
        }

        if (node.Intake.RiskLabels.Contains("state-and-worktree-mutation", StringComparer.OrdinalIgnoreCase))
        {
            capabilities.Add("operator-approval");
        }

        return capabilities.Count == 0 ? ["read-only"] : capabilities.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildVerificationContracts(GoalPlanNode node)
    {
        var contracts = node.Intake.Verification
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (node.Intake.TargetFiles.Any(path => path.StartsWith("src/", StringComparison.OrdinalIgnoreCase)))
        {
            contracts.Add("Repository test-impact plan must name focused tests or explain broad verification.");
        }

        return contracts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BuildRollbackBoundary(GoalPlanNode node)
    {
        var mutatesLifecycle = node.Intake.RiskLabels.Contains("state-and-worktree-mutation", StringComparer.OrdinalIgnoreCase);
        return mutatesLifecycle
            ? "isolated worktree plus explicit abandon/rollback gate before cleanup"
            : "isolated goal branch; acceptance gate before merge";
    }

    private static bool CanCreateGoal(
        GoalPlanNode node,
        string[] verificationContracts,
        ParallelExecutionDecision? decision)
    {
        return node.Intake.TargetFiles.Count > 0 &&
            verificationContracts.Length > 0 &&
            decision?.Disposition != ParallelExecutionDisposition.RequiresOperatorApproval;
    }

    private static IEnumerable<CompiledGoalValidationFinding> BuildValidationFindings(
        IReadOnlyList<GoalPlanNode> nodes,
        IReadOnlyList<CompiledGoalNode> compiledNodes,
        IReadOnlyList<GoalPlanEdge> edges,
        ParallelExecutionPlan parallel)
    {
        foreach (var node in compiledNodes)
        {
            if (node.FileScopes.Count == 0)
            {
                yield return new CompiledGoalValidationFinding("error", node.Id, "node has no declared file scope");
            }

            if (node.FileScopes.SequenceEqual(["BACKLOG.md"], StringComparer.OrdinalIgnoreCase))
            {
                yield return new CompiledGoalValidationFinding("warning", node.Id, "file scope is low confidence; inspect before dispatch");
            }

            if (node.VerificationContracts.Count == 0)
            {
                yield return new CompiledGoalValidationFinding("error", node.Id, "node has no verification contract");
            }

            if (node.ParallelDisposition == ParallelExecutionDisposition.RequiresOperatorApproval)
            {
                yield return new CompiledGoalValidationFinding("warning", node.Id, "parallel planner requires operator approval before dispatch");
            }
        }

        foreach (var edge in edges)
        {
            if (!nodes.Any(node => node.Id.Equals(edge.FromId, StringComparison.OrdinalIgnoreCase)) ||
                !nodes.Any(node => node.Id.Equals(edge.ToId, StringComparison.OrdinalIgnoreCase)))
            {
                yield return new CompiledGoalValidationFinding("error", edge.ToId, $"dependency edge references missing node {edge.FromId}->{edge.ToId}");
            }
        }

        if (parallel.Batches.Count == 0 && nodes.Count > 0)
        {
            yield return new CompiledGoalValidationFinding("error", "graph", "parallel planner produced no runnable batch");
        }
    }

    private static string BuildGraphId(
        IReadOnlyList<CompiledGoalNode> nodes,
        IReadOnlyList<GoalPlanEdge> edges)
    {
        var text = string.Join('|',
            nodes.Select(node => $"{node.Id}:{node.Heading}:{string.Join(',', node.FileScopes)}")
                .Concat(edges.Select(edge => $"{edge.FromId}>{edge.ToId}:{edge.Reason}")));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
    }

    private static string BuildReadyObjective(
        BacklogIntakeItem item,
        string[] dependencies,
        IReadOnlyList<GoalPlanEdge> edges)
    {
        if (dependencies.Length == 0)
        {
            return item.SuggestedObjective;
        }

        return string.Join(Environment.NewLine, [
            item.SuggestedObjective,
            string.Empty,
            "Explicit dependencies:",
            .. edges
                .Where(edge => dependencies.Contains(edge.FromId, StringComparer.OrdinalIgnoreCase))
                .Select(edge => $"- {edge.FromId}: {edge.Reason}")
        ]);
    }

    private static bool Mentions(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
