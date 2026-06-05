using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class DashboardRenderer
{
    private static void RenderGoalWorkModel(StringBuilder html, Goal goal, GoalVerificationGate verificationGate)
    {
        var verifiedTasks = verificationGate.Tasks.Count(task => task.GateStatus == VerificationGateStatus.Passed);
        var openVerificationTasks = verificationGate.Tasks.Count(task => task.GateStatus != VerificationGateStatus.Passed);
        var pendingInputCount = goal.Tasks.Count(task => task.Status == WorkTaskStatus.WaitingForHuman);

        html.AppendLine("<div class=\"goal-panel goal-panel-wide work-model-panel\">");
        html.AppendLine("<div class=\"work-model-head\">");
        html.AppendLine("<h3>How this goal moves forward</h3>");
        html.AppendLine("<span class=\"meta\">Goals contain tasks. Tasks produce proof. Verification decides whether the proof is enough for goal completion.</span>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"work-model-grid\">");
        RenderWorkModelStep(
            html,
            "1",
            "Goal",
            $"{goal.Tasks.Count} task(s)",
            "The outcome being managed. A goal is complete only after every task is verified and no human decision is pending.");
        RenderWorkModelStep(
            html,
            "2",
            "Tasks",
            $"{goal.Tasks.Count(task => task.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Running)} active or ready",
            "Each task belongs to a role, can run through a model or worker handoff, and records activity as it progresses.");
        RenderWorkModelStep(
            html,
            "3",
            "Verification",
            $"{verifiedTasks} passed, {openVerificationTasks} open",
            "Verification is the check that a task result is acceptable. It can be command-based or manually recorded.");
        RenderWorkModelStep(
            html,
            "4",
            "Human decisions",
            $"{pendingInputCount} task(s) waiting",
            "Some tasks pause until an operator answers a question. Those questions appear in the human decisions sections.");
        html.AppendLine("</div>");
        html.AppendLine("</div>");
    }

    private static void RenderWorkModelStep(StringBuilder html, string number, string title, string metric, string description)
    {
        html.AppendLine("<article class=\"work-model-step\">");
        html.AppendLine($"<div class=\"work-model-number\">{number}</div>");
        html.AppendLine("<div>");
        html.AppendLine($"<strong>{Encode(title)}</strong>");
        html.AppendLine($"<div class=\"meta\">{Encode(metric)}</div>");
        html.AppendLine($"<p>{Encode(description)}</p>");
        html.AppendLine("</div>");
        html.AppendLine("</article>");
    }
}
