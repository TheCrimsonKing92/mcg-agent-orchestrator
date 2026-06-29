using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalLandingPostActions
{
    public static void RunAdvisorySemanticAcceptance(
        Goal goal,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        WorkerProfileCatalog workerProfiles,
        string? worktreePath,
        AcceptanceVerificationResult? verification,
        Action<string>? writeLine = null)
    {
        if (worktreePath is null)
        {
            return;
        }

        if (HasSemanticAcceptanceReceipt(workspace.SemanticAcceptanceLogPath, goal.Id.Value))
        {
            writeLine?.Invoke($"Semantic acceptance (advisory): receipt already exists for goal {goal.Id.Value[..8]}; skipping judges.");
            return;
        }

        var modelFunctions = ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath);
        var baseJudges = SemanticAcceptanceEvaluator.BuildJudges(modelFunctions, providers, workerProfiles);
        if (baseJudges.Count == 0)
        {
            return;
        }

        try
        {
            var criteria = goal.Tasks
                .Select(task => task.VerificationPlan)
                .Where(plan => !string.IsNullOrWhiteSpace(plan))
                .Select(plan => plan!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var testSummary = verification?.Checks is { Count: > 0 } checks
                ? string.Join(Environment.NewLine, checks
                    .Where(check => !string.IsNullOrWhiteSpace(check.ResultSummary))
                    .Select(check => $"{check.Name}: {check.ResultSummary}"))
                : null;

            var perFileDiffs = GoalAcceptanceEvidenceBundleBuilder.GetPerFileDiffs(worktreePath);
            var inputs = new SemanticAcceptanceInputs(
                goal.Objective,
                criteria,
                GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath),
                GoalAcceptanceEvidenceBundleBuilder.GetDiffExcerpt(worktreePath),
                testSummary,
                perFileDiffs);

            var judges = baseJudges
                .Select(j => (ISemanticJudge)new RecursivePerFileSemanticJudge(j))
                .ToList();

            var report = SemanticAcceptanceEvaluator
                .EvaluateAsync(judges, inputs, TimeSpan.FromSeconds(90))
                .GetAwaiter()
                .GetResult();

            PrintSemanticAcceptanceReport(report, writeLine);
            AppendSemanticAcceptanceReceipt(workspace.SemanticAcceptanceLogPath, goal, report);
        }
        catch (Exception ex)
        {
            writeLine?.Invoke($"Semantic acceptance (advisory): skipped after error: {ex.Message}");
        }
    }

    public static bool AutoCloseSourceBacklogItem(
        Goal? goal,
        string backlogStorePath,
        Action<string>? writeLine = null)
    {
        if (goal?.SourceBacklogItemId is null)
        {
            return false;
        }

        try
        {
            var store = new BacklogStore(backlogStorePath);
            var closed = store.TryCloseByIdAsync(goal.SourceBacklogItemId, $"Goal {goal.Id.Value[..8]} landed.").GetAwaiter().GetResult();
            writeLine?.Invoke(closed
                ? $"Closed backlog item {goal.SourceBacklogItemId} (goal {goal.Id.Value[..8]} landed)."
                : $"Backlog item {goal.SourceBacklogItemId} already closed or not found (no-op).");
            return closed;
        }
        catch (Exception ex)
        {
            writeLine?.Invoke($"Warning: goal {goal.Id.Value[..8]} landed, but linked backlog item {goal.SourceBacklogItemId} was not closed: {ex.Message}");
            return false;
        }
    }

    private static void PrintSemanticAcceptanceReport(SemanticAcceptanceReport report, Action<string>? writeLine)
    {
        writeLine?.Invoke("Semantic acceptance (advisory - does not gate the merge):");
        foreach (var entry in report.Verdicts)
        {
            var verdict = entry.Verdict;
            if (!verdict.IsValid)
            {
                writeLine?.Invoke($"  {entry.Judge}: no verdict ({string.Join("; ", verdict.ValidationErrors)})");
                continue;
            }

            var summary = verdict.CriteriaMet ? "criteria MET" : "criteria NOT met";
            writeLine?.Invoke($"  {entry.Judge}: {summary} (confidence {verdict.Confidence})");
            foreach (var reason in verdict.Reasons.Take(3))
            {
                writeLine?.Invoke($"    - {reason}");
            }

            foreach (var unmet in verdict.UnmetCriteria)
            {
                writeLine?.Invoke($"    unmet: {unmet}");
            }
        }
    }

    private static void AppendSemanticAcceptanceReceipt(
        string semanticAcceptanceLogPath,
        Goal goal,
        SemanticAcceptanceReport report)
    {
        var receipt = new
        {
            at = DateTimeOffset.UtcNow,
            goalId = goal.Id.Value,
            objective = goal.Objective,
            consensus = report.Consensus,
            judges = report.Verdicts.Select(entry => new
            {
                judge = entry.Judge,
                valid = entry.Verdict.IsValid,
                criteriaMet = entry.Verdict.CriteriaMet,
                confidence = entry.Verdict.Confidence,
                reasons = entry.Verdict.Reasons,
                unmetCriteria = entry.Verdict.UnmetCriteria,
                errors = entry.Verdict.ValidationErrors
            })
        };

        var directory = Path.GetDirectoryName(semanticAcceptanceLogPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(semanticAcceptanceLogPath, JsonSerializer.Serialize(receipt) + Environment.NewLine);
    }

    private static bool HasSemanticAcceptanceReceipt(string semanticAcceptanceLogPath, string goalId)
    {
        if (!File.Exists(semanticAcceptanceLogPath))
        {
            return false;
        }

        foreach (var line in File.ReadLines(semanticAcceptanceLogPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("goalId", out var goalIdProperty)
                    && string.Equals(goalIdProperty.GetString(), goalId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Legacy/manual edits should not prevent new receipts from being written.
            }
        }

        return false;
    }
}
