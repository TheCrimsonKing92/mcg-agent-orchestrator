using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class LandingExecutor
{
    internal static (ProspectiveEvidenceGateResolution Resolution, string? HoldDescription, IReadOnlyList<string> ObligationLines) EvaluatePendingHumanInput(
        AgentOrchestratorKernel kernel, Goal goal, OrchestratorWorkspace workspace, string? candidateRevision)
    {
        var orchestratorDirectory = workspace.OrchestratorDirectory;
        // Resolve candidate-bound gate and role reports before projecting pending input.
        var resolution = ProspectiveEvidenceGateResolver.Resolve(kernel, goal, orchestratorDirectory, candidateRevision);
        IReadOnlyDictionary<HumanInputRequestId, string> skipReasons = new Dictionary<HumanInputRequestId, string>();
        if (!string.IsNullOrWhiteSpace(candidateRevision))
            skipReasons = kernel.CloseRoleReportEvidence(goal.Id, candidateRevision,
                RoleReportGateClassEvidence.For(orchestratorDirectory, goal.Id, candidateRevision),
                RemovedTestClassSource(workspace, goal.Id, candidateRevision),
                (reviewed, landing) => ConductorOnlyCandidateRange.IsConductorOnly(
                    workspace.ResolveExecutionDirectory(goal.Id), workspace.IntegrationBranch, reviewed, landing));
        var obligations = GateTestObligationEvaluator.Evaluate(goal, orchestratorDirectory, candidateRevision,
            new(workspace.ExecutionDirectory, workspace.IntegrationBranch), kernel.HumanInputRequests.ToArray());
        foreach (var result in obligations.Where(result => result.Outcome == GateTestObligationEvaluator.Outcome.Satisfied &&
            result.RenameProvenance.Length > 0))
            Console.WriteLine(result.RenameProvenance);
        foreach (var result in obligations.Where(result => result.Outcome == GateTestObligationEvaluator.Outcome.Satisfied &&
            result.AnswerRebindProvenance.Length > 0))
            Console.WriteLine(result.AnswerRebindProvenance);
        foreach (var line in obligations.SelectMany(result => result.PackageResolutionProvenance))
            Console.WriteLine(line);
        var obligationLines = obligations
            .Where(result => result.Outcome is not (GateTestObligationEvaluator.Outcome.Satisfied or GateTestObligationEvaluator.Outcome.Waived))
            .Select(result =>
            {
                var outcome = result.Outcome switch
                {
                    GateTestObligationEvaluator.Outcome.Pending => "pending",
                    GateTestObligationEvaluator.Outcome.SelectorNotFound => "selector-not-found",
                    GateTestObligationEvaluator.Outcome.Failed => "failed",
                    _ => throw new InvalidOperationException($"Unexpected obligation outcome: {result.Outcome}")
                };
                var selectors = result.Selectors.Count == 0 ? "none" : string.Join(", ", result.Selectors);
                return $"gate test obligation {result.Obligation.Id[..Math.Min(8, result.Obligation.Id.Length)]} " +
                    $"(brief v{result.Obligation.BriefVersion}) {outcome}: {selectors}; source {result.SourceId ?? "none"}";
            }).ToArray();
        var pending = PostLandingOperatorCheck.HoldingLanding(kernel.GetPendingHumanInput(goal.Id));
        if (pending.Count == 0)
            return (resolution, obligationLines.Length == 0 ? null : string.Join("; ", obligationLines), obligationLines);

        var requests = pending.OrderBy(wait => wait.Id.Value, StringComparer.Ordinal).Select(wait =>
        {
            var unresolved = resolution.UnresolvedWaits.SingleOrDefault(item => item.RequestId == wait.Id);
            var detail = unresolved?.SubmissionFailure is { } failure
                ? $"; submission failed: {failure}"
                : unresolved is not null
                    ? $"; missing: {string.Join(", ", unresolved.MissingOrFailedTestNames)}"
                    : skipReasons.TryGetValue(wait.Id, out var reason)
                        ? $"; owner round not accepted as evidence: {reason}"
                        : "; answer required through SubmitHumanInput";
            return $"request {wait.Id.Value} ({wait.Kind}, owner {wait.EvidenceOwner ?? "unspecified"}){detail}";
        });
        var hold = $"goal {goal.Id.Value} has pending human input: {string.Join("; ", requests)}";
        if (obligationLines.Length > 0) hold += "; " + string.Join("; ", obligationLines);
        return (resolution, hold, obligationLines);
    }

    internal static string? DescribeGroupedPendingHumanInput(
        AgentOrchestratorKernel kernel, IReadOnlyList<Goal> goals, OrchestratorWorkspace workspace,
        Func<GoalId, string> candidateRevision)
    {
        var holds = goals.Select(goal => EvaluatePendingHumanInput(
                kernel, goal, workspace, candidateRevision(goal.Id)).HoldDescription)
            .Where(description => description is not null).ToArray();
        return holds.Length == 0 ? null : string.Join("; ", holds);
    }

    private static Func<string, string?> RemovedTestClassSource(
        OrchestratorWorkspace workspace, GoalId goalId, string candidate)
    {
        var root = workspace.ResolveExecutionDirectory(goalId);
        var baseRevision = new Lazy<GitCli.GitResult>(() =>
            GitCli.Run(root, "merge-base", workspace.IntegrationBranch, candidate));
        return name =>
        {
            if (!SyntaxFacts.IsValidIdentifier(name)) return null;
            var mergeBase = baseRevision.Value;
            if (!mergeBase.Succeeded || mergeBase.DrainTimedOut) return null;
            var revision = mergeBase.Output.Trim();
            if (revision.Length == 0) return null;
            var matches = GitCli.Run(root, "grep", "-l", "-w", "-F", "-e", name, revision, "--", "*.cs");
            if (!matches.Succeeded || matches.DrainTimedOut) return null;
            var paths = new List<string>();
            var classNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in matches.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith(revision + ":", StringComparison.Ordinal)) return null;
                var path = line[(revision.Length + 1)..];
                var source = GitCli.Run(root, "show", $"{revision}:{path}");
                if (!source.Succeeded || source.DrainTimedOut) return null;
                var syntax = CSharpSyntaxTree.ParseText(source.Output).GetRoot();
                if (syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error)) return null;
                var declarations = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Where(node => node.Identifier.ValueText == name).ToArray();
                if (declarations.Length == 0) continue;
                foreach (var node in declarations)
                {
                    var namespaces = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                        .Reverse().Select(item => item.Name.ToString());
                    var containing = node.Ancestors().OfType<ClassDeclarationSyntax>()
                        .Reverse().Select(item => item.Identifier.ValueText);
                    classNames.Add(string.Join(".", namespaces.Append(string.Join("+", containing.Append(name)))));
                }
                paths.Add(path);
                // Successful tree inspection proves absence; a failed Git command proves nothing.
                var remaining = GitCli.Run(root, "ls-tree", "--name-only", candidate, "--", ":(literal)" + path);
                if (!remaining.Succeeded || remaining.DrainTimedOut || !string.IsNullOrWhiteSpace(remaining.Output)) return null;
            }
            return paths.Count > 0 && classNames.Count == 1
                ? $"base {revision} paths {string.Join(",", paths)}" : null;
        };
    }
}
