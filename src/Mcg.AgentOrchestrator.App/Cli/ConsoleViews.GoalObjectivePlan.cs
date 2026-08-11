using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    private static readonly JsonSerializerOptions GoalObjectivePlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void PrintGoalObjectivePlan(GoalObjectivePlan plan)
    {
        Console.WriteLine("Goal objective plan:");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            objective = plan.Objective,
            workflow = plan.Workflow,
            disposition = plan.Disposition.ToString(),
            estimatedComplexity = plan.EstimatedComplexity.ToString(),
            historicalTimeEstimate = plan.HistoricalTimeEstimate,
            historicalOutcomeRates = plan.HistoricalOutcomeRates,
            riskLabels = plan.RiskLabels,
            capabilityWarnings = plan.CapabilityWarnings,
            pipelineDecision = new
            {
                pipeline = plan.PipelineDecision.Pipeline.ToString(),
                workflow = plan.PipelineDecision.Workflow,
                isOverride = plan.PipelineDecision.IsOverride,
                selectionSource = plan.PipelineDecision.SelectionSource,
                orderedRoles = plan.TaskBoundaries.Select(boundary => boundary.Role.ToString()),
                reasons = plan.PipelineDecision.Reasons
            },
            fileScopes = plan.FileScopes,
            requiredTools = plan.RequiredTools,
            requiredVerification = plan.RequiredVerification,
            taskBoundaries = plan.TaskBoundaries.Select(boundary => new
            {
                boundary.Index,
                role = boundary.Role.ToString(),
                boundary.Purpose,
                boundary.Capability,
                boundary.Verification
            }),
            canCreateGoal = plan.CanCreateGoal,
            recommendation = plan.Recommendation
        }, GoalObjectivePlanJsonOptions));
    }
}
