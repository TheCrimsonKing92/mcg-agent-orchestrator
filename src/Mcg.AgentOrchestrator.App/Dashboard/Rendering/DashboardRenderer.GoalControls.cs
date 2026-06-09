using System.Net;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class DashboardRenderer
{
    private static void RenderGoalOperatorControls(StringBuilder html, AgentOrchestratorKernel kernel, Goal goal)
    {
        var goalPrefix = Encode(goal.Id.Value[..8]);
        html.AppendLine("<div class=\"goal-actions goal-control-card\">");
        html.AppendLine("<div class=\"goal-control-card-head\">");
        html.AppendLine("<strong>Operate this goal</strong>");
        html.AppendLine("<span class=\"meta\">Run the next step, hand work to local tools, or add follow-up tasks.</span>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"goal-control-grid\">");
        html.AppendLine("<section class=\"goal-control-group goal-control-group-wide\">");
        html.AppendLine("<h4>Quick reports</h4>");
        html.AppendLine("<p class=\"section-note\">Human-readable previews with links to the full raw output.</p>");
        RenderGoalDataViewPreviews(html, kernel, goal, goalPrefix);
        html.AppendLine("</section>");
        html.AppendLine("<section class=\"goal-control-group goal-control-group-dispatch\">");
        html.AppendLine("<h4>Goal automation</h4>");
        html.AppendLine("<p class=\"section-note\">Use these when you want the orchestrator to choose or assign the next goal-level step.</p>");
        html.AppendLine("<div class=\"buttonbar\">");
        html.AppendLine($"<button type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/advance\">Run next safe action</button>");
        html.AppendLine($"<button type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/advance-subscription?confirmSubscriptionAdvance=true\">Run next subscription action</button>");
        html.AppendLine($"<button class=\"primary\" type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/advance-subscription-until-blocked?confirmSubscriptionAdvance=true\">Continue subscription handoff</button>");
        html.AppendLine($"<button type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/advance-until-blocked\">Continue non-API actions</button>");
        html.AppendLine($"<button type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/delegate\">Assign tasks to agents</button>");
        html.AppendLine("</div>");
        html.AppendLine("</section>");
        html.AppendLine("<section class=\"goal-control-group\">");
        html.AppendLine("<h4>Worker handoff</h4>");
        html.AppendLine("<p class=\"section-note\">Prepare command files or start work that runs outside the dashboard.</p>");
        html.AppendLine($"<form class=\"controls compact\" data-action=\"/api/goals/{goalPrefix}/profile-dispatch-ready\">");
        html.AppendLine($"<div class=\"field\"><label for=\"profile-{goalPrefix}\">Worker profile</label><input id=\"profile-{goalPrefix}\" name=\"profileName\" value=\"local-echo\" required></div>");
        html.AppendLine("<button type=\"submit\">Prepare worker handoffs</button>");
        html.AppendLine("</form>");
        html.AppendLine("<div class=\"buttonbar\">");
        html.AppendLine($"<button type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/subscription-dispatch-ready\">Prepare subscription handoffs</button>");
        html.AppendLine($"<button type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/start-subscription-ready?confirmBatchStart=true\">Start subscription work</button>");
        html.AppendLine($"<button type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/start-dispatches?confirmBatchStart=true\">Start prepared work</button>");
        html.AppendLine($"<button type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/refresh-dispatches\">Refresh running work</button>");
        html.AppendLine($"<button type=\"button\" data-action-button=\"/api/goals/{goalPrefix}/cancel-dispatches\">Cancel running work</button>");
        html.AppendLine("</div>");
        html.AppendLine("</section>");
        html.AppendLine("<section class=\"goal-control-group goal-control-group-planning\">");
        html.AppendLine("<h4>Add work or ask</h4>");
        html.AppendLine("<p class=\"section-note\">Create a task under this goal or ask a question that must be answered before work can continue.</p>");
        html.AppendLine("<div class=\"goal-control-stack\">");
        html.AppendLine($"<form class=\"controls wide-form\" data-action=\"/api/goals/{goalPrefix}/tasks\">");
        html.AppendLine($"<div class=\"field\"><label for=\"task-role-{goalPrefix}\">Role</label><select id=\"task-role-{goalPrefix}\" name=\"role\"><option>Developer</option><option>Planner</option><option>Researcher</option><option>Tester</option><option>Reviewer</option></select></div>");
        html.AppendLine("<div class=\"field\"><label>Description</label><input class=\"wide\" name=\"description\" required></div>");
        html.AppendLine("<div class=\"field\"><label>Verification plan</label><input class=\"wide\" name=\"verificationPlan\"></div>");
        html.AppendLine("<input type=\"hidden\" name=\"delegate\" value=\"false\">");
        html.AppendLine($"<label class=\"checkrow\" for=\"task-delegate-{goalPrefix}\"><input id=\"task-delegate-{goalPrefix}\" type=\"checkbox\" name=\"delegate\" value=\"true\" checked>Assign now</label>");
        html.AppendLine("<button type=\"submit\">Add Task</button>");
        html.AppendLine("</form>");
        html.AppendLine($"<form class=\"controls compact\" data-action=\"/api/goals/{goalPrefix}/ask\">");
        html.AppendLine("<div class=\"field\"><label>Goal question</label><input class=\"wide\" name=\"question\" required></div>");
        html.AppendLine("<button type=\"submit\">Ask at goal level</button>");
        html.AppendLine("</form>");
        html.AppendLine("</div>");
        html.AppendLine("</section>");
        html.AppendLine("</div>");
        html.AppendLine("</div>");
    }

    private static void RenderPendingInputForm(StringBuilder html, Goal goal, HumanInputRequest request)
    {
        var goalPrefix = Encode(goal.Id.Value[..8]);
        var requestPrefix = Encode(request.Id.Value[..8]);
        var formAnchor = Encode(BuildHumanInputFormAnchor(request.Id));
        var task = request.TaskId is null
            ? null
            : goal.Tasks.FirstOrDefault(candidate => candidate.Id == request.TaskId);
        var scope = task is null ? "Goal" : $"Task {GetTaskDisplayNumber(goal, task.Id)} &middot; {task.RequiredRole}";

        html.AppendLine($"<form id=\"{formAnchor}\" class=\"answer-form\" data-action=\"/api/input/{requestPrefix}/answer\">");
        html.AppendLine($"<div class=\"meta\">Pending decision &middot; {scope} &middot; requested {Encode(request.RequestedAt.ToString("u"))}</div>");
        html.AppendLine($"<p class=\"decision-question\">{Encode(request.Question)}</p>");
        html.AppendLine("<div class=\"context-grid\">");
        html.AppendLine("<div class=\"context-block\">");
        html.AppendLine("<h4>Decision Scope</h4>");
        html.AppendLine($"<div><strong>Request</strong> {requestPrefix}</div>");
        if (task is null)
        {
            html.AppendLine($"<div><strong>Goal</strong> {Encode(goal.Objective)}</div>");
            html.AppendLine($"<div><strong>Status</strong> {Encode(Display(goal.Status))}</div>");
            html.AppendLine("<div class=\"context-actions\">");
            html.AppendLine($"<a href=\"/api/goals/{goalPrefix}/transcript\" target=\"_blank\" rel=\"noreferrer\">Activity log</a>");
            html.AppendLine($"<a href=\"/api/monitor?goal={goalPrefix}\" target=\"_blank\" rel=\"noreferrer\">Monitor</a>");
            html.AppendLine("</div>");
        }
        else
        {
            var taskNumber = GetTaskDisplayNumber(goal, task.Id);
            var taskPrefix = $"/api/goals/{goalPrefix}/tasks/{taskNumber}";
            html.AppendLine($"<div><strong>Status</strong> {Encode(Display(task.Status))}</div>");
            html.AppendLine($"<div><strong>Description</strong> {Encode(task.Description)}</div>");
            html.AppendLine($"<div><strong>Verification</strong> {RenderVerificationPlan(task)}</div>");
            html.AppendLine("<div class=\"context-actions\">");
            html.AppendLine($"<a href=\"{taskPrefix}/brief\" target=\"_blank\" rel=\"noreferrer\">Brief</a>");
            html.AppendLine($"<a href=\"{taskPrefix}/timeline\" target=\"_blank\" rel=\"noreferrer\">Timeline</a>");
            html.AppendLine($"<a href=\"{taskPrefix}/gate\" target=\"_blank\" rel=\"noreferrer\">Verification status</a>");
            html.AppendLine($"<a href=\"{taskPrefix}/verifications\" target=\"_blank\" rel=\"noreferrer\">Verification records</a>");
            if (task.LastProcess is not null)
            {
                html.AppendLine($"<a href=\"{taskPrefix}/logs\" target=\"_blank\" rel=\"noreferrer\">Logs</a>");
            }

            html.AppendLine("</div>");
        }

        html.AppendLine("</div>");
        if (task is not null)
        {
            html.AppendLine("<div class=\"context-block\">");
        html.AppendLine("<h4>Latest proof</h4>");
            html.AppendLine(RenderEvidence(task));
            html.AppendLine("</div>");
            html.AppendLine("<div class=\"context-block\">");
        html.AppendLine("<h4>Recent task activity</h4>");
            var events = goal.Timeline
                .Where(evt => evt.TaskId == task.Id)
                .OrderByDescending(evt => evt.OccurredAt)
                .Take(4)
                .OrderBy(evt => evt.OccurredAt)
                .ToList();
            if (events.Count == 0)
            {
                html.AppendLine("<span class=\"meta\">No task timeline entries.</span>");
            }
            else
            {
                html.AppendLine("<ul class=\"context-list\">");
                foreach (var evt in events)
                {
                    html.AppendLine($"<li><span class=\"meta\">{Encode(evt.OccurredAt.ToString("u"))}</span> {Encode(Display(evt.Kind))}: {Encode(evt.Message)}</li>");
                }

                html.AppendLine("</ul>");
            }

            html.AppendLine("</div>");
        }

        html.AppendLine("</div>");
        if (IsYesNoQuestion(request.Question))
        {
            html.AppendLine("<div class=\"answer-choice-row\" aria-label=\"Quick answers\">");
            html.AppendLine("<button class=\"primary\" type=\"submit\" name=\"answer\" value=\"Yes\">Yes</button>");
            html.AppendLine("<button type=\"submit\" name=\"answer\" value=\"No\">No</button>");
            html.AppendLine($"<button type=\"button\" data-toggle-custom-answer=\"answer-{requestPrefix}\" aria-expanded=\"false\">Write custom answer</button>");
            html.AppendLine("</div>");
            html.AppendLine("<div class=\"controls answer-controls custom-answer\" hidden>");
            html.AppendLine($"<div class=\"field\"><label for=\"answer-{requestPrefix}\">Custom answer</label><textarea id=\"answer-{requestPrefix}\" name=\"answer\" required disabled></textarea></div>");
            html.AppendLine("<button class=\"primary\" type=\"submit\">Submit Answer</button>");
            html.AppendLine("</div>");
        }
        else
        {
            html.AppendLine("<div class=\"controls answer-controls\">");
            html.AppendLine($"<div class=\"field\"><label for=\"answer-{requestPrefix}\">Answer</label><textarea id=\"answer-{requestPrefix}\" name=\"answer\" required></textarea></div>");
            html.AppendLine("<button class=\"primary\" type=\"submit\">Submit Answer</button>");
            html.AppendLine("</div>");
        }

        html.AppendLine("</form>");
    }

    private static string RenderTaskActions(Goal goal, TaskSpec task, DashboardRenderOptions options)
    {
        var goalPrefix = Encode(goal.Id.Value[..8]);
        var taskNumber = GetTaskDisplayNumber(goal, task.Id);
        var prefix = $"/api/goals/{goalPrefix}/tasks/{taskNumber}";
        var html = new StringBuilder();
        html.AppendLine("<div class=\"task-action-card\">");
        html.AppendLine("<div class=\"task-action-card-head\">");
        html.AppendLine($"<strong>Task {taskNumber} controls</strong>");
        html.AppendLine($"<span class=\"meta\">{task.RequiredRole} &middot; {Encode(Display(task.Status))}</span>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"task-action-panel\">");
        html.AppendLine("<section class=\"task-action-group task-action-group-wide task-primary-action\">");
        html.AppendLine("<h4>Current next action</h4>");
        if (task.LastProcess is { IsRunning: true })
        {
            html.AppendLine($"<p>Background process is running: pid {task.LastProcess.ProcessId}</p>");
            html.AppendLine($"<p class=\"meta\"><code>{Encode(task.LastProcess.Command)}</code></p>");
            html.AppendLine("<div class=\"buttonbar\">");
            html.AppendLine($"<button class=\"primary\" type=\"button\" data-action-button=\"{prefix}/refresh\">Refresh process</button>");
            html.AppendLine($"<button type=\"button\" data-action-button=\"{prefix}/cancel\">Cancel process</button>");
            html.AppendLine("</div>");
        }
        else if (task.Status == WorkTaskStatus.Running && task.LastDispatch is not null)
        {
            html.AppendLine($"<p>Prepared handoff for {Encode(task.LastDispatch.WorkerName)}.</p>");
            html.AppendLine($"<p class=\"meta\"><code>{Encode(task.LastDispatch.Command)}</code></p>");
            html.AppendLine("<div class=\"buttonbar\">");
            html.AppendLine($"<button class=\"primary\" type=\"button\" data-action-button=\"{prefix}/start?confirmDispatchStart=true\">Start prepared work</button>");
            html.AppendLine($"<a href=\"{prefix}/brief\" target=\"_blank\" rel=\"noreferrer\">Review brief</a>");
            html.AppendLine("</div>");
        }
        else if (task.Status == WorkTaskStatus.Completed && task.LastVerification is { Succeeded: true })
        {
            html.AppendLine("<p class=\"ok\">This task is complete and has passing verification evidence.</p>");
        }
        else
        {
            var actionLabel = task.Status == WorkTaskStatus.Completed
                ? "Record passing evidence"
                : "Complete and record passing evidence";
            html.AppendLine($"<form class=\"controls compact\" data-action=\"{prefix}/complete-verify\">");
            html.AppendLine("<input type=\"hidden\" name=\"passed\" value=\"true\">");
            html.AppendLine("<div class=\"field\"><label>Completion evidence</label><input class=\"wide\" name=\"note\" required></div>");
            html.AppendLine($"<button class=\"primary\" type=\"submit\">{actionLabel}</button>");
            html.AppendLine("</form>");
        }

        html.AppendLine("</section>");
        html.AppendLine("<details class=\"task-advanced-controls\">");
        html.AppendLine("<summary>Advanced task controls</summary>");
        html.AppendLine("<div class=\"task-action-panel task-action-panel-advanced\">");
        html.AppendLine("<section class=\"task-action-group task-action-group-wide\">");
        html.AppendLine("<h4>Inspect and run task</h4>");
        html.AppendLine("<div class=\"task-actions\">");
        html.AppendLine($"<a href=\"{prefix}/brief\" target=\"_blank\" rel=\"noreferrer\">Brief</a>");
        html.AppendLine($"<a href=\"{prefix}/timeline\" target=\"_blank\" rel=\"noreferrer\">Activity</a>");
        html.AppendLine($"<a href=\"{prefix}/gate\" target=\"_blank\" rel=\"noreferrer\">Verification status</a>");
        html.AppendLine($"<a href=\"{prefix}/verification-plan\" target=\"_blank\" rel=\"noreferrer\">Verification plan</a>");
        html.AppendLine($"<a href=\"{prefix}/verifications\" target=\"_blank\" rel=\"noreferrer\">Verification records</a>");
        html.AppendLine($"<button type=\"button\" data-action-button=\"{prefix}/run?confirmTaskRun=true\">{Encode(DashboardNextActionControls.GetRunActionLabel(goal, task.Id, options.HealthReport?.Agents))}</button>");
        if (DashboardNextActionControls.CanRunApiExplicitly(goal, task.Id, options.HealthReport?.Agents))
        {
            html.AppendLine($"<button type=\"button\" data-action-button=\"{prefix}/api-run?confirmTaskRun=true\">Explicit API run</button>");
        }

        RenderAdvancedProcessAction(html, prefix, "start?confirmDispatchStart=true", "Start prepared work", GetStartDispatchReadiness(task));
        RenderAdvancedProcessAction(html, prefix, "refresh", "Refresh process", GetRefreshProcessReadiness(task));
        RenderAdvancedProcessAction(html, prefix, "cancel", "Cancel process", GetCancelProcessReadiness(task));
        if (task.LastProcess is not null)
        {
            html.AppendLine($"<a href=\"{prefix}/logs\" target=\"_blank\" rel=\"noreferrer\">Logs</a>");
        }

        html.AppendLine("</div>");
        html.AppendLine("</section>");
        html.AppendLine("<section class=\"task-action-group task-action-group-wide\">");
        html.AppendLine("<h4>Retry</h4>");
        html.AppendLine($"<form class=\"controls compact\" data-action=\"{prefix}/retry\">");
        html.AppendLine("<div class=\"field\"><label>Retry note</label><input class=\"wide\" name=\"message\" placeholder=\"What changed or what should be tried next?\" required></div>");
        html.AppendLine("<button type=\"submit\">Retry task</button>");
        html.AppendLine("</form>");
        html.AppendLine("</section>");
        html.AppendLine("<section class=\"task-action-group\">");
        html.AppendLine("<h4>Worker handoff</h4>");
        html.AppendLine("<div class=\"buttonbar\">");
        html.AppendLine($"<button type=\"button\" data-action-button=\"{prefix}/subscription-dispatch\">Prepare subscription handoff</button>");
        html.AppendLine("</div>");
        html.AppendLine($"<form class=\"controls compact\" data-action=\"{prefix}/profile-dispatch\">");
        html.AppendLine($"<div class=\"field\"><label for=\"task-profile-{goalPrefix}-{taskNumber}\">Worker profile</label><input id=\"task-profile-{goalPrefix}-{taskNumber}\" name=\"profileName\" value=\"codex-cli\" required></div>");
        html.AppendLine("<button type=\"submit\">Prepare handoff</button>");
        html.AppendLine("</form>");
        html.AppendLine("</section>");
        html.AppendLine("<section class=\"task-action-group\">");
        html.AppendLine("<h4>Progress and questions</h4>");
        html.AppendLine($"<form class=\"controls compact\" data-action=\"{prefix}/progress\">");
        html.AppendLine($"<div class=\"field\"><label for=\"task-status-{goalPrefix}-{taskNumber}\">Status</label><select id=\"task-status-{goalPrefix}-{taskNumber}\" name=\"status\"><option value=\"running\">Running</option><option value=\"completed\">Completed</option><option value=\"failed\">Failed</option><option value=\"cancelled\">Cancelled</option></select></div>");
        html.AppendLine("<div class=\"field\"><label>Progress note</label><input name=\"message\" required></div>");
        html.AppendLine("<button type=\"submit\">Report progress</button>");
        html.AppendLine("</form>");
        html.AppendLine($"<form class=\"controls compact\" data-action=\"{prefix}/ask\">");
        html.AppendLine("<div class=\"field\"><label>Human question</label><input name=\"question\" required></div>");
        html.AppendLine("<button type=\"submit\">Ask human</button>");
        html.AppendLine("</form>");
        html.AppendLine("</section>");
        html.AppendLine("<section class=\"task-action-group task-action-group-verification\">");
        html.AppendLine("<h4>Verification proof</h4>");
        html.AppendLine($"<form class=\"controls compact\" data-action=\"{prefix}/verification-plan\">");
        html.AppendLine("<div class=\"field\"><label>Verification plan</label><input class=\"wide\" name=\"plan\" required></div>");
        html.AppendLine("<button type=\"submit\">Update Plan</button>");
        html.AppendLine("</form>");
        html.AppendLine($"<form class=\"controls compact\" data-action=\"{prefix}/verify\">");
        html.AppendLine("<div class=\"field\"><label>Verify command</label><input name=\"command\" placeholder=\"dotnet test --filter 'AgentCatalog|WorkerProfile'\" required></div>");
        html.AppendLine("<button type=\"submit\">Run verification</button>");
        html.AppendLine("</form>");
        html.AppendLine("<p class=\"section-note\">PowerShell: quote filters that contain | so they stay command arguments.</p>");
        html.AppendLine($"<form class=\"controls compact\" data-action=\"{prefix}/verify-manual\">");
        html.AppendLine("<div class=\"field\"><label>Manual result</label><select name=\"passed\"><option value=\"true\">Passed</option><option value=\"false\">Failed</option></select></div>");
        html.AppendLine("<div class=\"field\"><label>Verification note</label><input name=\"note\" required></div>");
        html.AppendLine("<button type=\"submit\">Record manual result</button>");
        html.AppendLine("</form>");
        html.AppendLine("</section>");
        html.AppendLine("</div>");
        html.AppendLine("</details>");
        html.AppendLine("</div>");
        html.AppendLine("</div>");
        return html.ToString();
    }

    private static void RenderAdvancedProcessAction(
        StringBuilder html,
        string prefix,
        string operation,
        string label,
        TaskActionReadiness readiness)
    {
        if (readiness.IsReady)
        {
            html.AppendLine($"<button type=\"button\" data-action-button=\"{prefix}/{operation}\">{label}</button>");
            return;
        }

        html.AppendLine(
            $"<span class=\"disabled-action\"><button type=\"button\" disabled title=\"{Encode(readiness.Reason)}\">{label} unavailable</button><span class=\"meta\">{Encode(readiness.Reason)}</span></span>");
    }

    private static TaskActionReadiness GetStartDispatchReadiness(TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Running)
        {
            return TaskActionReadiness.NotReady($"Task status is {task.Status}; only running dispatched tasks can be started.");
        }

        if (task.LastDispatch is null)
        {
            return TaskActionReadiness.NotReady("Task has no recorded dispatch.");
        }

        if (task.LastProcess is { IsRunning: true })
        {
            return TaskActionReadiness.NotReady($"Task already has a running process pid={task.LastProcess.ProcessId}.");
        }

        return TaskActionReadiness.Ready;
    }

    private static TaskActionReadiness GetRefreshProcessReadiness(TaskSpec task)
    {
        if (task.LastProcess is null)
        {
            return TaskActionReadiness.NotReady("Task has no background process to refresh.");
        }

        if (!task.LastProcess.IsRunning)
        {
            return TaskActionReadiness.NotReady($"Task process already completed with exit={task.LastProcess.ExitCode?.ToString() ?? "n/a"}.");
        }

        return TaskActionReadiness.Ready;
    }

    private static TaskActionReadiness GetCancelProcessReadiness(TaskSpec task)
    {
        if (task.LastProcess is null)
        {
            return TaskActionReadiness.NotReady("Task has no background process to cancel.");
        }

        if (!task.LastProcess.IsRunning)
        {
            return TaskActionReadiness.NotReady($"Task process already completed with exit={task.LastProcess.ExitCode?.ToString() ?? "n/a"}.");
        }

        return TaskActionReadiness.Ready;
    }

    private sealed record TaskActionReadiness(bool IsReady, string Reason)
    {
        public static TaskActionReadiness Ready { get; } = new(true, string.Empty);

        public static TaskActionReadiness NotReady(string reason) => new(false, reason);
    }
}
