using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintGoalIntakeReceipt(GoalIntakeRequestRecord record)
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        kind = "goal-intake-receipt",
        requestKey = record.RequestKey,
        state = record.State,
        createdAt = record.CreatedAt,
        updatedAt = record.UpdatedAt,
        goalId = record.GoalId,
        failureCode = record.FailureCode,
        failureDetail = record.FailureDetail,
        stdoutPath = record.StdoutPath,
        stderrPath = record.StderrPath
    }));
}

public static void PrintHealth(OrchestratorHealthReport report)
{
    Console.WriteLine($"Ready: {report.IsReady}");
    Console.WriteLine("Providers:");
    foreach (var provider in report.Providers)
    {
        Console.WriteLine($"  {provider.ProviderName}: configured={provider.IsConfigured} mode={provider.Mode} ({provider.Detail})");
    }

    Console.WriteLine("Agents:");
    foreach (var agent in report.Agents)
    {
        Console.WriteLine($"  {agent.Role}: valid={agent.IsValid} execution={agent.ExecutionPolicy} api={agent.ProviderName}/{agent.ModelName} reasoning={agent.ReasoningEffort ?? "default"} subscription={agent.SubscriptionProfileName ?? "none"} alternate-ready={agent.HasSubscriptionCapableAlternate} alternates={agent.SubscriptionCapableAlternatesDetail} ({agent.Detail})");
    }

    Console.WriteLine("Worker profiles:");
    foreach (var profile in report.WorkerProfiles)
    {
        Console.WriteLine($"  {profile.Name}: executable={profile.Executable} resolvable={profile.IsResolvable} ({profile.Detail})");
    }
}

public static void PrintGoal(
    Goal goal,
    string? friendlyLabel = null,
    string? statusText = null,
    PortfolioMembership? portfolio = null,
    bool tasksOnly = false)
{
    Console.WriteLine();
    var label = string.IsNullOrWhiteSpace(friendlyLabel) ? string.Empty : $" ({friendlyLabel.Trim().ReplaceLineEndings(" ")})";
    Console.WriteLine($"Goal {goal.Id}{label}");
    if (!tasksOnly)
    {
        Console.WriteLine($"Objective: {OutputTextPreview.CreateSummary(goal.Objective).Text}");
    }

    Console.WriteLine($"Status: {statusText ?? goal.Status.ToString()}");
    if (!tasksOnly && !string.IsNullOrWhiteSpace(goal.SourceBacklogItemId))
    {
        Console.WriteLine($"Source backlog: {goal.SourceBacklogItemId}");
        Console.WriteLine($"Source backlog coverage: {goal.SourceBacklogCoverage?.ToString().ToLowerInvariant() ?? "legacy-full"}");
    }

    if (!tasksOnly && portfolio is not null)
    {
        var project = string.IsNullOrWhiteSpace(portfolio.ProjectTitle) ? "unassigned" : $"{portfolio.ProjectTitle} ({portfolio.ProjectId![..Math.Min(8, portfolio.ProjectId.Length)]})";
        Console.WriteLine($"Portfolio: epic={portfolio.EpicTitle} ({portfolio.EpicId[..Math.Min(8, portfolio.EpicId.Length)]}) project={project}");
    }

    if (!tasksOnly && goal.EffectiveAcceptanceCriteriaCorrections.Count > 0)
    {
        Console.WriteLine("Effective acceptance criteria corrections:");
        foreach (var correction in goal.EffectiveAcceptanceCriteriaCorrections.OrderByDescending(item => item.RecordedAt))
        {
            var source = correction.SourceTaskId is null ? "goal" : ShortId(correction.SourceTaskId.Value);
            Console.WriteLine($"  - supersedes: {OutputTextPreview.CreateSummary(correction.SupersededCriterion).Text}");
            Console.WriteLine(correction.IsWaiver
                ? $"    waiver reason: {OutputTextPreview.CreateSummary(correction.WaiverReason!).Text}"
                : $"    correction: {OutputTextPreview.CreateSummary(correction.Correction).Text}");
            Console.WriteLine($"    provenance: {correction.Actor} {correction.RecordedAt:u} {correction.SourceKind} {source}");
        }
    }

    if (!tasksOnly && goal.CriterionEvidenceObligations.Count > 0)
    {
        Console.WriteLine("Criterion evidence obligations:");
        foreach (var obligation in goal.CriterionEvidenceObligations.OrderBy(item => item.CriterionVersion).ThenBy(item => item.CriterionIndex))
        {
            Console.WriteLine($"  - {obligation.Id}: owner={obligation.Owner.ToString().ToLowerInvariant()} state={obligation.State.ToString().ToLowerInvariant()} next_action={obligation.RequiredScope} provenance={obligation.Provenance}");
            Console.WriteLine(
                $"    mapped_candidate={obligation.ExpectedCandidateSha ?? "unbound"} " +
                $"receipt={obligation.ReceiptId ?? "pending"} receipt_candidate={obligation.CandidateSha ?? "pending"} " +
                $"finding={obligation.FindingStableId ?? "none"}");
            if (!string.IsNullOrWhiteSpace(obligation.Detail))
                Console.WriteLine($"    evidence_detail={OutputTextPreview.CreateSummary(obligation.Detail).Text}");
        }
    }

    if (!tasksOnly)
    {
        var declaredCriteria = AcceptanceCriteriaParser.ParseDeclared(goal.Objective);
        if (declaredCriteria.Count > 0 || goal.RefinedSpec is not null)
        {
            Console.WriteLine($"Brief acceptance criteria ({declaredCriteria.Count}):");
            for (var index = 0; index < declaredCriteria.Count; index++)
            {
                Console.WriteLine($"  {index + 1}. {declaredCriteria[index].Trim().ReplaceLineEndings(" ")}");
            }

            if (goal.RefinedSpec is not { } refinedSpec)
            {
                Console.WriteLine($"Refined acceptance criteria: pending refinement (brief declares {declaredCriteria.Count})");
            }
            else
            {
                Console.WriteLine($"Refined acceptance criteria ({refinedSpec.AcceptanceCriteria.Count}):");
                for (var index = 0; index < refinedSpec.AcceptanceCriteria.Count; index++)
                {
                    Console.WriteLine($"  {index + 1}. {refinedSpec.AcceptanceCriteria[index].Trim().ReplaceLineEndings(" ")}");
                }

                if (declaredCriteria.Count > 0 && declaredCriteria.Count != refinedSpec.AcceptanceCriteria.Count)
                {
                    Console.WriteLine(
                        $"WARNING: refined acceptance criteria count {refinedSpec.AcceptanceCriteria.Count} " +
                        $"differs from brief declared count {declaredCriteria.Count}.");
                }
            }
        }
    }

    Console.WriteLine("Tasks:");

    for (var index = 0; index < goal.Tasks.Count; index++)
    {
        var task = goal.Tasks[index];
        var assignment = task.AssignedAgentId is null ? "unassigned" : ShortId(task.AssignedAgentId.Value);
        Console.WriteLine($"  {index + 1}. [{task.Status}] {task.RequiredRole}: {OutputTextPreview.CreateSummary(task.Description).Text} ({assignment})");
    }

    Console.WriteLine();
}

public static void PrintTaskQueryResult(Goal goal, TaskQueryResult result)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {result.GoalId.Value[..8]} {result.Status}: {OutputTextPreview.CreateSummary(result.Objective).Text}");
    Console.WriteLine($"Tasks matched: {result.Tasks.Count}");

    foreach (var task in result.Tasks)
    {
        var assignment = task.AssignedAgentId is null ? "unassigned" : ShortId(task.AssignedAgentId.Value);
        Console.WriteLine($"  {GetTaskDisplayNumber(goal, task.Id)}. {task.Id.Value[..8]} [{task.Status}] {task.RequiredRole}: {OutputTextPreview.CreateSummary(task.Description).Text} ({assignment}) evidence={FormatTaskEvidence(task)}");
    }

    Console.WriteLine();
}

public static string FormatTaskEvidence(TaskSpec task)
{
    if (task.LastVerification is not null)
    {
        return task.LastVerification.Succeeded ? "passed-verification" : "failed-verification";
    }

    if (task.LastProcess is not null)
    {
        if (task.LastProcess.IsRunning)
        {
            return "running-process";
        }

        return task.LastProcess.WasCancelled ? "cancelled-process" : "completed-process";
    }

    if (task.LastDispatch is not null)
    {
        return "dispatch";
    }

    if (task.LastExecution is not null)
    {
        return "execution";
    }

    return "none";
}

private static string ShortId(string value) => value[..Math.Min(8, value.Length)];
}


