using System.Text;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class DashboardRenderer
{
    private static void RenderGoalDataViewPreviews(
        StringBuilder html,
        AgentOrchestratorKernel kernel,
        Goal goal,
        string goalPrefix,
        DashboardRenderOptions options)
    {
        var monitor = kernel.BuildMonitor(goal.Id);
        var nextActions = kernel.BuildNextActions(goal.Id);
        var actionRecommendations = BuildDashboardActionRecommendations(kernel, goal, options);
        var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
        var evidence = kernel.BuildGoalEvidenceSummary(goal.Id);
        var stages = kernel.BuildStageReadinessReport(goal.Id);
        var verificationGate = kernel.BuildVerificationGate(goal.Id);
        var verificationWorklist = kernel.BuildVerificationWorklist(goal.Id);
        var humanInputWorklist = kernel.BuildHumanInputWorklist(goal.Id);

        html.AppendLine("<div class=\"report-preview-grid\">");
        RenderReportPreview(
            html,
            "Action recommendation",
            actionRecommendations?.Primary is null
                ? "No primary action"
                : $"{Encode(actionRecommendations.Primary.Title)} &middot; {(actionRecommendations.Primary.CanApply ? "can apply" : "operator gate")}",
            BuildActionRecommendationPreviewDetails(actionRecommendations),
            $"/api/goals/{goalPrefix}/action-recommendations",
            "Open recommendation JSON",
            "report-preview-card-wide");

        RenderReportPreview(
            html,
            "Monitor",
            $"{Display(monitor.Status)} &middot; {monitor.TotalTasks} task(s)",
            [
                $"Attention: {monitor.AttentionItems.Count}",
                $"Pending input: {monitor.PendingHumanInputCount}",
                $"Last event: {Encode(monitor.LastTimelineEventAt?.ToString("u") ?? "n/a")}"
            ],
            $"/api/monitor?goal={goalPrefix}",
            "Open monitor JSON");

        RenderReportPreview(
            html,
            "Work summary",
            $"{goal.Tasks.Count} task(s) &middot; {monitor.PendingHumanInputCount} pending input",
            [
                $"Verified: {goal.Tasks.Count(task => task.LastVerification?.Succeeded is true)}",
                $"Processes: {goal.Tasks.Count(task => task.LastProcess is not null)}",
                $"Dispatches: {goal.Tasks.Count(task => task.LastDispatch is not null)}"
            ],
            $"/api/goals/{goalPrefix}/work-summary",
            "Open compact JSON");

        RenderReportPreview(
            html,
            "Failure triage",
            "Policy-aware failure causes",
            [
                "Classifies retry-after, provider failures, file locks, dirty worktrees, and stale branches.",
                "Includes policy-gated next commands."
            ],
            $"/api/goals/{goalPrefix}/failure-triage",
            "Open triage JSON");

        RenderReportPreview(
            html,
            "Next",
            $"{nextActions.Items.Count} queued action(s)",
            nextActions.Items.Take(3).Select(item => $"{Encode(Display(item.Kind))}: {Encode(TrimPreview(item.Message))}").ToList(),
            $"/api/next?goal={goalPrefix}",
            "Open next-action JSON",
            "report-preview-card-wide");

        RenderReportPreview(
            html,
            "Goal completion",
            acceptance.IsAccepted ? "Accepted" : "Not accepted",
            [
                $"Tasks passed: {acceptance.PassedTasks}/{acceptance.TotalTasks}",
                $"Blockers: {acceptance.Blockers.Count}",
                $"Open verification: {acceptance.OpenVerificationCount}"
            ],
            $"/api/acceptance?goal={goalPrefix}",
            "Open blockers JSON");

        RenderReportPreview(
            html,
            "Recorded proof",
            $"{evidence.TasksWithVerification}/{evidence.TotalTasks} task(s) verified",
            BuildRecordedProofPreviewDetails(evidence),
            $"/api/evidence?goal={goalPrefix}",
            "Open evidence JSON");

        RenderReportPreview(
            html,
            "Task readiness",
            stages.IsReadyForAcceptance ? "Ready for acceptance" : "Not ready",
            [
                $"Verified: {stages.VerifiedStages}/{stages.TotalStages}",
                $"Open: {stages.OpenStages}",
                $"Blocked: {stages.BlockedStages}"
            ],
            $"/api/stages?goal={goalPrefix}",
            "Open readiness JSON");

        RenderReportPreview(
            html,
            "Verification status",
            verificationGate.IsSatisfied ? "Satisfied" : "Needs verification",
            [
                $"Passed: {verificationGate.Tasks.Count(task => task.GateStatus == VerificationGateStatus.Passed)}",
                $"Open: {verificationGate.Tasks.Count(task => task.GateStatus != VerificationGateStatus.Passed)}"
            ],
            $"/api/gates?goal={goalPrefix}",
            "Open gate JSON");

        RenderReportPreview(
            html,
            "Verification to-do",
            $"{verificationWorklist.OpenCount} open item(s)",
            verificationWorklist.Items.Take(2).Select(item => $"Task {TaskDisplayNumber.Resolve(goal, item.TaskId)}: {Encode(TrimPreview(item.SuggestedAction))}").ToList(),
            $"/api/verification-worklist?goal={goalPrefix}",
            "Open verification JSON",
            "report-preview-card-wide");

        RenderReportPreview(
            html,
            "Human decisions",
            $"{humanInputWorklist.OpenCount} pending request(s)",
            humanInputWorklist.Items.Take(2).Select(item => $"{Encode(item.RequestId.Value[..8])}: {Encode(TrimPreview(item.Question))}").ToList(),
            $"/api/human-input-worklist?goal={goalPrefix}",
            "Open decisions JSON",
            "report-preview-card-wide");

        RenderReportPreview(
            html,
            "Source survey",
            "Low-noise repository map",
            [
                "Excludes: bin, obj, .scratch, prototype state",
                "<code>rg --files -g \"!**/bin/**\" -g \"!**/obj/**\" -g \"!**/.scratch/**\" -g \"!**/.orchestrator-prototype/**\"</code>"
            ],
            "/api/source-survey?max=8",
            "Open survey",
            "report-preview-card-wide");

        RenderReportPreview(
            html,
            "Subscription Plan",
            $"{goal.Tasks.Count(task => task.AssignedAgentId is not null)}/{goal.Tasks.Count} assigned task(s)",
            [
                $"Ready tasks: {goal.Tasks.Count(task => task.Status == WorkTaskStatus.Assigned)}",
                "Profile checks are shown in the full plan."
            ],
            $"/api/goals/{goalPrefix}/subscription-plan",
            "Open plan");

        RenderReportPreview(
            html,
            "Activity log",
            $"{goal.Timeline.Count} timeline event(s)",
            goal.Timeline
                .OrderByDescending(item => item.OccurredAt)
                .Take(2)
                .Select(item => $"{Encode(item.OccurredAt.ToString("u"))}: {Encode(Display(item.Kind))}")
                .ToList(),
            $"/api/goals/{goalPrefix}/transcript",
            "Open full log",
            "report-preview-card-wide");
        html.AppendLine("</div>");
    }

    private static DashboardActionRecommendationReport? BuildDashboardActionRecommendations(
        AgentOrchestratorKernel kernel,
        Goal goal,
        DashboardRenderOptions options)
    {
        if (options.Workspace is null)
        {
            return null;
        }

        return DashboardActionRecommendationPlanner.Build(
            kernel,
            goal,
            options.AgentDefinitions ?? [],
            options.WorkerProfiles ?? WorkerProfileCatalog.Default(),
            options.Workspace.ExecutionDirectory,
            AutonomyPolicy.Default);
    }

    private static List<string> BuildActionRecommendationPreviewDetails(
        DashboardActionRecommendationReport? report)
    {
        if (report?.Primary is not { } primary)
        {
            return ["No recommendation sources available."];
        }

        var details = new List<string>
        {
            $"Command: <code>{Encode(primary.SuggestedCommand)}</code>"
        };
        if (!string.IsNullOrWhiteSpace(primary.ApiPath))
        {
            details.Add($"API: {Encode(primary.ApiMethod ?? "GET")} <code>{Encode(primary.ApiPath)}</code>");
        }

        details.Add($"Reason: {Encode(TrimPreview(primary.Reason))}");
        details.Add($"Policy: {Encode(report.PolicyName)}; secondary: {report.Secondary.Count}");
        return details;
    }

    private static List<string> BuildRecordedProofPreviewDetails(GoalEvidenceSummary evidence)
    {
        var details = new List<string>
        {
            $"Execution: {evidence.TasksWithExecution}",
            $"Tokens: {FormatTokenUsage(evidence.InputTokens, evidence.OutputTokens)}",
            $"Potentially paid: {FormatTokenUsage(evidence.PotentiallyPaidInputTokens, evidence.PotentiallyPaidOutputTokens)}",
            $"Dispatch: {evidence.TasksWithDispatch}",
            $"Running processes: {evidence.RunningProcesses}"
        };

        details.AddRange(evidence.ModelUsage.Take(2).Select(usage => $"Model: {RenderModelUsageSummary(usage)}"));
        if (evidence.ModelUsage.Count > 2)
        {
            details.Add($"Models: +{evidence.ModelUsage.Count - 2} more");
        }
        details.AddRange(evidence.DispatchModelUsage.Take(2).Select(dispatch => $"Dispatch model: {RenderDispatchModelSummary(dispatch)}"));
        if (evidence.DispatchModelUsage.Count > 2)
        {
            details.Add($"Dispatch models: +{evidence.DispatchModelUsage.Count - 2} more");
        }
        details.AddRange(evidence.ModelFit.Take(2).Select(fit => $"Model fit: {RenderModelFitSummary(fit)}"));
        if (evidence.ModelFit.Count > 2)
        {
            details.Add($"Model fit: +{evidence.ModelFit.Count - 2} more");
        }

        return details;
    }

    private static void RenderReportPreview(
        StringBuilder html,
        string title,
        string summary,
        List<string> details,
        string href,
        string linkText,
        string? cssClass = null)
    {
        var classes = string.IsNullOrWhiteSpace(cssClass)
            ? "report-preview-card"
            : $"report-preview-card {cssClass}";
        html.AppendLine($"<article class=\"{classes}\">");
        html.AppendLine("<div class=\"report-preview-card-head\">");
        html.AppendLine($"<strong>{Encode(title)}</strong>");
        html.AppendLine($"<a href=\"{href}\" target=\"_blank\" rel=\"noreferrer\">{Encode(linkText)}</a>");
        html.AppendLine("</div>");
        html.AppendLine($"<p>{Encode(summary)}</p>");
        if (details.Count == 0)
        {
            html.AppendLine("<div class=\"meta\">No preview items.</div>");
        }
        else
        {
            html.AppendLine("<div class=\"report-preview-list\">");
            foreach (var detail in details)
            {
                html.AppendLine($"<div class=\"report-preview-detail\">{detail}</div>");
            }

            html.AppendLine("</div>");
        }

        html.AppendLine("</article>");
    }

    private static string TrimPreview(string value)
    {
        const int maxLength = 96;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, maxLength - 1), "...");
    }
}
