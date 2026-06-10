using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record PaidSubscriptionPromptRisk(
    int TaskCount,
    int PromptCharacterCount,
    int BatchPromptThreshold,
    int BatchTaskThreshold,
    bool PromptExceedsBatchThreshold,
    bool TaskCountExceedsThreshold,
    bool HasOversizedPrompt,
    bool UsesComplexPaidModel,
    IReadOnlyList<string> Details);

internal static class SubscriptionPromptCostGuard
{
public const string DashboardConfirmationQueryName = "confirmLargePaidSubscriptionStart";
public const string CliConfirmationFlag = "--confirm-large-paid-subscription-start";
private const int SimplePaidPromptThreshold = 4000;
private const int ComplexPaidPromptThreshold = 6000;
private const int BatchPaidPromptThreshold = 12000;
private const int BatchPaidTaskThreshold = 3;

public static PaidSubscriptionPromptRisk? EvaluateReadySubscriptionStart(
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    Func<TaskSpec, int?> estimatePromptCharacterCount,
    TaskSpec? onlyTask = null)
{
    var plan = DashboardResponseMapper.BuildSubscriptionPlan(goal, agents, profiles, estimatePromptCharacterCount);
    var candidates = plan.Items
        .Where(item => item.CanPrepare &&
            (onlyTask is null || item.TaskId.Equals(onlyTask.Id.Value, StringComparison.Ordinal)) &&
            item.EstimatedPromptCharacterCount is not null &&
            !string.IsNullOrWhiteSpace(item.ProviderName) &&
            ProviderSmokeRunner.IsPaidProviderName(item.ProviderName))
        .Select(item => new PaidPromptCandidate(
            item.TaskNumber,
            item.ProviderName!,
            item.SubscriptionModelName ?? item.SubscriptionModelAlias ?? item.ModelName ?? "default",
            item.TaskComplexity,
            item.EstimatedPromptCharacterCount!.Value))
        .ToList();

    return BuildRisk(candidates);
}

public static PaidSubscriptionPromptRisk? EvaluateReadySubscriptionStart(IReadOnlyList<SubscriptionPlanItemDto> items)
{
    var candidates = items
        .Where(item => item.CanPrepare &&
            item.EstimatedPromptCharacterCount is not null &&
            !string.IsNullOrWhiteSpace(item.ProviderName) &&
            ProviderSmokeRunner.IsPaidProviderName(item.ProviderName))
        .Select(item => new PaidPromptCandidate(
            item.TaskNumber,
            item.ProviderName!,
            item.SubscriptionModelName ?? item.SubscriptionModelAlias ?? item.ModelName ?? "default",
            item.TaskComplexity,
            item.EstimatedPromptCharacterCount!.Value))
        .ToList();

    return BuildRisk(candidates);
}

public static PaidSubscriptionPromptRisk? EvaluatePreparedDispatchStart(AgentOrchestratorKernel kernel, Goal goal, TaskSpec? onlyTask = null)
{
    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.StartDispatches);
    var readyTaskIds = plan.Items
        .Where(item => item.Status == ProcessBatchItemStatus.Ready)
        .Select(item => item.TaskId)
        .ToHashSet();

    var candidates = goal.Tasks
        .Where(task => readyTaskIds.Contains(task.Id) && (onlyTask is null || task.Id == onlyTask.Id))
        .Where(task => task.LastDispatch is not null &&
            task.LastDispatch.PromptCharacterCount is not null &&
            !string.IsNullOrWhiteSpace(task.LastDispatch.ProviderName) &&
            ProviderSmokeRunner.IsPaidProviderName(task.LastDispatch.ProviderName))
        .Select(task => new PaidPromptCandidate(
            ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
            task.LastDispatch!.ProviderName!,
            task.LastDispatch.ModelName ?? "default",
            task.LastDispatch.TaskComplexity,
            task.LastDispatch.PromptCharacterCount!.Value))
        .ToList();

    return BuildRisk(candidates);
}

public static PaidSubscriptionPromptRisk? EvaluatePreparedDispatchStart(Goal goal, TaskSpec task)
{
    if (task.LastDispatch is null ||
        task.LastDispatch.PromptCharacterCount is null ||
        string.IsNullOrWhiteSpace(task.LastDispatch.ProviderName) ||
        !ProviderSmokeRunner.IsPaidProviderName(task.LastDispatch.ProviderName))
    {
        return null;
    }

    return BuildRisk(
        [
            new PaidPromptCandidate(
                ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
                task.LastDispatch.ProviderName,
                task.LastDispatch.ModelName ?? "default",
                task.LastDispatch.TaskComplexity,
                task.LastDispatch.PromptCharacterCount.Value)
        ]);
}

public static void ThrowIfConfirmationRequired(PaidSubscriptionPromptRisk? risk, bool confirmed)
{
    if (risk is null || confirmed)
    {
        return;
    }

    throw new InvalidOperationException(BuildCliMessage(risk));
}

public static string BuildDashboardMessage(PaidSubscriptionPromptRisk risk)
{
    return BuildMessage(
        risk,
        $"add {DashboardConfirmationQueryName}=true after inspecting the subscription plan");
}

public static string BuildInlineLabel(PaidSubscriptionPromptRisk risk)
{
    if (risk.PromptExceedsBatchThreshold || risk.HasOversizedPrompt)
    {
        return "large paid subscription start";
    }

    if (risk.TaskCountExceedsThreshold)
    {
        return "paid subscription fanout";
    }

    if (risk.UsesComplexPaidModel)
    {
        return "complex paid subscription model";
    }

    return "paid subscription start";
}

private static string BuildCliMessage(PaidSubscriptionPromptRisk risk)
{
    return BuildMessage(
        risk,
        $"rerun with {CliConfirmationFlag} after inspecting subscription-plan");
}

private static string BuildMessage(PaidSubscriptionPromptRisk risk, string confirmationInstruction)
{
    var details = risk.Details.Count == 0
        ? string.Empty
        : " " + string.Join(" ", risk.Details);
    return $"Paid subscription start requires explicit confirmation: {risk.PromptCharacterCount} prompt chars across {risk.TaskCount} task(s), thresholds {risk.BatchPromptThreshold} chars or {risk.BatchTaskThreshold} task(s). {confirmationInstruction}.{details}";
}

private static PaidSubscriptionPromptRisk? BuildRisk(IReadOnlyList<PaidPromptCandidate> candidates)
{
    if (candidates.Count == 0)
    {
        return null;
    }

    var total = candidates.Sum(candidate => candidate.PromptCharacterCount);
    var oversized = candidates
        .Where(candidate => candidate.PromptCharacterCount > PromptThreshold(candidate.TaskComplexity))
        .ToList();
    var complex = candidates
        .Where(candidate => candidate.TaskComplexity == TaskComplexity.Complex)
        .ToList();
    var tooManyPaidTasks = candidates.Count > BatchPaidTaskThreshold;

    if (total <= BatchPaidPromptThreshold &&
        !tooManyPaidTasks &&
        oversized.Count == 0 &&
        complex.Count == 0)
    {
        return null;
    }

    var details = new List<string>();
    if (total > BatchPaidPromptThreshold)
    {
        details.Add($"Batch prompt chars {total} exceed {BatchPaidPromptThreshold}.");
    }

    if (tooManyPaidTasks)
    {
        details.Add($"Paid task count {candidates.Count} exceeds {BatchPaidTaskThreshold}.");
    }

    details.AddRange(oversized.Take(3).Select(candidate =>
        $"Task {candidate.TaskNumber} {candidate.ProviderName}/{candidate.ModelName} prompt {candidate.PromptCharacterCount} chars exceeds {PromptThreshold(candidate.TaskComplexity)}."));

    if (oversized.Count > 3)
    {
        details.Add($"+{oversized.Count - 3} more oversized task prompt(s).");
    }

    details.AddRange(complex.Take(3).Select(candidate =>
        $"Task {candidate.TaskNumber} {candidate.ProviderName}/{candidate.ModelName} uses complex paid model selection."));

    if (complex.Count > 3)
    {
        details.Add($"+{complex.Count - 3} more complex paid task(s).");
    }

    return new PaidSubscriptionPromptRisk(
        candidates.Count,
        total,
        BatchPaidPromptThreshold,
        BatchPaidTaskThreshold,
        total > BatchPaidPromptThreshold,
        tooManyPaidTasks,
        oversized.Count > 0,
        complex.Count > 0,
        details);
}

private static int PromptThreshold(TaskComplexity? complexity)
{
    return complexity == TaskComplexity.Complex
        ? ComplexPaidPromptThreshold
        : SimplePaidPromptThreshold;
}

private sealed record PaidPromptCandidate(
    int TaskNumber,
    string ProviderName,
    string ModelName,
    TaskComplexity? TaskComplexity,
    int PromptCharacterCount);
}
