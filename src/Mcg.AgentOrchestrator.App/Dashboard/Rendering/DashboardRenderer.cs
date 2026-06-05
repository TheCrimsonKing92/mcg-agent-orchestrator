using System.Net;
using System.Text;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public sealed record DashboardRenderOptions(
    int? AutoRefreshSeconds = null,
    bool EnableOperatorControls = false,
    OrchestratorHealthReport? HealthReport = null,
    DashboardWorkspaceContext? Workspace = null,
    IReadOnlyList<DashboardContinuationStatusDto>? ContinuationWatches = null,
    string? FocusGoalPrefix = null);

public sealed record DashboardWorkspaceContext(
    string RootDirectory,
    string StatePath,
    string ExecutionDirectory,
    string PromptDirectory,
    string LogDirectory,
    string WorkerProfilePath,
    string AgentCatalogPath,
    int DashboardProcessId,
    string DashboardRestartCommand,
    DashboardProcessDiagnostic? ProcessDiagnostic = null,
    string? DashboardBindUrl = null,
    string? DashboardPageUrl = null,
    IReadOnlyList<string>? HostedDashboardPageUrls = null,
    string? SourceSurveyUrl = null,
    IReadOnlyList<string>? HostedSourceSurveyUrls = null,
    string? HostedAccessNote = null,
    IReadOnlyList<DashboardBuildTestRunSummaryDto>? BuildTestRuns = null)
{
    public DashboardWorkspaceContext(
        string rootDirectory,
        string statePath,
        string executionDirectory,
        string promptDirectory,
        string logDirectory,
        string workerProfilePath,
        string agentCatalogPath,
        int dashboardProcessId)
        : this(
            rootDirectory,
            statePath,
            executionDirectory,
            promptDirectory,
            logDirectory,
            workerProfilePath,
            agentCatalogPath,
            dashboardProcessId,
            "serve-dashboard",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
    {
    }
}

public sealed record DashboardProcessDiagnostic(
    int CurrentProcessId,
    string ProcessName,
    string? CurrentExecutablePath,
    IReadOnlyList<int> CurrentListeningPorts,
    IReadOnlyList<DashboardSiblingProcessContext> SiblingProcesses,
    string Message);

public sealed record DashboardSiblingProcessContext(
    int ProcessId,
    string? ExecutablePath,
    DateTimeOffset? StartedAt,
    IReadOnlyList<int> ListeningPorts,
    string SafeStopCommand,
    string Detail);

public static partial class DashboardRenderer
{
    public static string Render(AgentOrchestratorKernel kernel, DashboardRenderOptions? options = null)
    {
        options ??= new DashboardRenderOptions();
        var goals = kernel.Goals
            .OrderByDescending(goal => goal.Timeline.LastOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
            .ToList();
        var displayedGoals = GetDisplayedGoals(goals, options);

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        if (options.AutoRefreshSeconds is > 0 && !options.EnableOperatorControls)
        {
            html.AppendLine($"<meta http-equiv=\"refresh\" content=\"{options.AutoRefreshSeconds.Value}\">");
        }
        html.AppendLine("<title>MCG Agent Orchestrator</title>");
        if (options.EnableOperatorControls)
        {
            html.AppendLine("<link rel=\"stylesheet\" href=\"/assets/dashboard.css\">");
        }
        else
        {
            html.AppendLine("<style>");
            html.AppendLine(DashboardAssets.Styles);
            html.AppendLine("</style>");
        }
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        var isReadOnlyHosted = options.Workspace is not null && !options.EnableOperatorControls;
        var refreshMode = options.EnableOperatorControls ? "Auto-update" : "Auto-refresh";
        var mode = options.AutoRefreshSeconds is > 0
            ? isReadOnlyHosted
                ? $"Read-only hosted dashboard; {refreshMode.ToLowerInvariant()} every {options.AutoRefreshSeconds.Value} seconds"
                : $"{refreshMode} every {options.AutoRefreshSeconds.Value} seconds"
            : isReadOnlyHosted
                ? "Read-only hosted dashboard"
                : "Static dashboard export";
        html.AppendLine($"<header><h1>MCG Agent Orchestrator</h1><div>{Encode(mode)}</div></header>");
        var refreshAttribute = options.AutoRefreshSeconds is > 0
            ? $" data-refresh-seconds=\"{options.AutoRefreshSeconds.Value}\""
            : string.Empty;
        html.AppendLine($"<main id=\"dashboard-content\"{refreshAttribute}>");
        if (options.EnableOperatorControls)
        {
            RenderGlobalOperatorControls(html, options.Workspace, options.ContinuationWatches ?? []);
        }
        else if (options.Workspace is not null)
        {
            RenderReadOnlyHostedContext(html, options.Workspace);
        }

        if (options.HealthReport is not null)
        {
            RenderHealthReport(html, options.HealthReport, options.EnableOperatorControls);
        }

        if (goals.Count == 0)
        {
            html.AppendLine(options.EnableOperatorControls
                ? "<section><h2>No goals</h2><p class=\"meta\">No active goals yet.</p></section>"
                : "<section><h2>No goals</h2><p>Create a goal from the CLI to populate this dashboard.</p></section>");
        }
        else if (options.EnableOperatorControls && displayedGoals.Count < goals.Count)
        {
            var focusNote = string.IsNullOrWhiteSpace(options.FocusGoalPrefix)
                ? string.Empty
                : $" Focused goal filter: <code>{Encode(options.FocusGoalPrefix)}</code>.";
            html.AppendLine($"<section><h2>Recent Goals</h2><p class=\"meta\">Showing {displayedGoals.Count} of {goals.Count} goals.{focusNote} Use goal JSON links, <code>?goal=&lt;prefix&gt;</code>, or report endpoints for older goals.</p></section>");
        }

        if (options.EnableOperatorControls && goals.Count > 0)
        {
            RenderGoalArchiveControl(html, goals, options.FocusGoalPrefix);
        }

        foreach (var goal in displayedGoals)
        {
            var monitor = kernel.BuildMonitor(goal.Id);
            var verificationGate = kernel.BuildVerificationGate(goal.Id);
            var evidence = kernel.BuildGoalEvidenceSummary(goal.Id);
            html.AppendLine("<section class=\"goal-card\">");
            html.AppendLine("<div class=\"goal-header\">");
            html.AppendLine($"<h2>{Encode(goal.Objective)}</h2>");
            var goalJsonLink = options.Workspace is not null
                ? $" &middot; <a href=\"/api/goals/{Encode(goal.Id.Value[..8])}\" target=\"_blank\" rel=\"noopener\">Goal JSON</a>"
                : string.Empty;
            html.AppendLine($"<div class=\"meta\">Goal {Encode(goal.Id.Value)} &middot; <span class=\"pill\">{Encode(Display(goal.Status))}</span> &middot; Last event {Encode(monitor.LastTimelineEventAt?.ToString("u") ?? "n/a")}{goalJsonLink}</div>");
            html.AppendLine("</div>");
            if (goal.Status == GoalStatus.Completed && verificationGate.IsSatisfied)
            {
                html.AppendLine("<div class=\"completion-banner\">");
                html.AppendLine("<strong>Goal complete</strong>");
                if (IsManualOnlyCompletion(evidence))
                {
                    html.AppendLine($"<span>{goal.Tasks.Count} task(s) completed with manual-only verification. No execution, dispatch, or process proof is recorded; inspect the verification note before treating this as implemented work.</span>");
                }
                else
                {
                    html.AppendLine($"<span>{goal.Tasks.Count} task(s) completed and verified. No operator action is required.</span>");
                }

                html.AppendLine("</div>");
            }

            if (options.EnableOperatorControls)
            {
                RenderGoalOperatorControls(html, kernel, goal);
            }

            RenderGoalWorkModel(html, goal, verificationGate);
            RenderSubscriptionRetryQueue(html, goal);

            html.AppendLine("<div class=\"goal-overview-grid\">");
            html.AppendLine("<div class=\"goal-panel\">");
            html.AppendLine("<h3>Task status</h3>");
            html.AppendLine("<p class=\"section-note\">Current state of each task under this goal.</p>");
            html.AppendLine("<table><thead><tr><th>Status</th><th>Count</th></tr></thead><tbody>");
            foreach (var count in monitor.TaskStatusCounts)
            {
                html.AppendLine($"<tr><td>{Encode(Display(count.Status))}</td><td>{count.Count}</td></tr>");
            }
            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");

            html.AppendLine("<div class=\"goal-panel\">");
            html.AppendLine("<h3>Needs attention</h3>");
            html.AppendLine("<p class=\"section-note\">Items that may block or delay progress.</p>");
            if (monitor.AttentionItems.Count == 0)
            {
                html.AppendLine("<p class=\"ok\">No attention items.</p>");
            }
            else
            {
                foreach (var item in monitor.AttentionItems)
                {
                    var task = item.TaskId is null ? "goal" : item.TaskId.Value[..8];
                    html.AppendLine($"<div class=\"attention\"><strong>{Encode(Display(item.Kind))}</strong> <span class=\"meta\">{Encode(task)}</span><br>{Encode(item.Message)}</div>");
                }
            }
            html.AppendLine("</div>");
            html.AppendLine("</div>");

            html.AppendLine("<div class=\"goal-work-grid\">");
            html.AppendLine("<div class=\"goal-panel\">");
            html.AppendLine("<h3>Human decisions</h3>");
            html.AppendLine("<p class=\"section-note\">Questions waiting for an operator answer.</p>");
            var humanInputWorklist = kernel.BuildHumanInputWorklist(goal.Id);
            html.AppendLine($"<p class=\"{(humanInputWorklist.OpenCount == 0 ? "ok" : "bad")}\">Open: {humanInputWorklist.OpenCount}</p>");
            html.AppendLine("<table><thead><tr><th>Request</th><th>Work item</th><th>Question</th><th>Command</th></tr></thead><tbody>");
            if (humanInputWorklist.Items.Count == 0)
            {
                html.AppendLine("<tr><td colspan=\"4\">none</td></tr>");
            }
            else
            {
                foreach (var item in humanInputWorklist.Items)
                {
                    var answerLink = options.EnableOperatorControls
                        ? $"<br><a class=\"button-link\" href=\"#{Encode(BuildHumanInputFormAnchor(item.RequestId))}\">Answer</a>"
                        : string.Empty;
                    html.AppendLine("<tr>");
                    html.AppendLine($"<td>{Encode(item.RequestId.Value[..8])}</td><td>{RenderWorkItemReference(goal, item.TaskId, item.Role, item.TaskStatus, item.Description)}</td><td>{Encode(item.Question)}<br><span class=\"meta\">{Encode(item.SuggestedAction)}</span></td><td><code>{Encode(BuildHumanInputSuggestedCommand(item.RequestId))}</code>{answerLink}</td>");
                    html.AppendLine("</tr>");
                }
            }
            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");

            html.AppendLine("<div class=\"goal-panel\">");
            html.AppendLine("<h3>Recommended next steps</h3>");
            html.AppendLine("<p class=\"section-note\">The next actions the system believes can move the goal forward.</p>");
            var nextActionControlHeader = options.EnableOperatorControls ? "<th>Control</th>" : string.Empty;
            html.AppendLine($"<table><thead><tr><th>Priority</th><th>Work item</th><th>Action</th><th>Suggested Command</th>{nextActionControlHeader}</tr></thead><tbody>");
            var nextActions = kernel.BuildNextActions(goal.Id);
            for (var index = 0; index < nextActions.Items.Count; index++)
            {
                var item = nextActions.Items[index];
                html.AppendLine("<tr>");
                html.AppendLine($"<td>{index + 1}</td><td>{RenderWorkItemReference(goal, item.TaskId)}</td><td>{Encode(Display(item.Kind))}<br><span class=\"meta\">{Encode(item.Message)}</span></td><td><code>{Encode(BuildSuggestedCommand(goal, item))}</code></td>");
                if (options.EnableOperatorControls)
                {
                    html.AppendLine($"<td>{RenderNextActionControl(goal, item)}</td>");
                }

                html.AppendLine("</tr>");
            }
            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");
            html.AppendLine("</div>");

            html.AppendLine("<div class=\"goal-readiness-grid\">");
            html.AppendLine("<div class=\"goal-panel\">");
            html.AppendLine("<h3>Goal completion</h3>");
            html.AppendLine("<p class=\"section-note\">A goal completes when every task has passed verification and no human decision is pending.</p>");
            var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
            html.AppendLine($"<p class=\"{(acceptance.IsAccepted ? "ok" : "bad")}\">Accepted: {acceptance.IsAccepted} &middot; Tasks passed: {acceptance.PassedTasks}/{acceptance.TotalTasks} &middot; Open verification: {acceptance.OpenVerificationCount} &middot; Pending input: {acceptance.PendingHumanInputCount}</p>");
            if (acceptance.Blockers.Count == 0)
            {
                html.AppendLine("<p class=\"ok\">No acceptance blockers.</p>");
            }
            else
            {
                html.AppendLine("<table><thead><tr><th>Scope</th><th>Blocker</th><th>Action</th><th>Command</th></tr></thead><tbody>");
                foreach (var blocker in acceptance.Blockers)
                {
                    int? taskNumber = blocker.TaskId is null ? null : GetTaskDisplayNumber(goal, blocker.TaskId);
                    var scope = taskNumber is null ? "goal" : $"task {taskNumber}";
                    html.AppendLine("<tr>");
                    html.AppendLine($"<td>{Encode(scope)}</td><td>{Encode(Display(blocker.Kind))}<br><span class=\"meta\">{Encode(blocker.Message)}</span></td><td>{Encode(blocker.SuggestedAction)}</td><td><code>{Encode(BuildAcceptanceSuggestedCommand(blocker, taskNumber))}</code></td>");
                    html.AppendLine("</tr>");
                }
                html.AppendLine("</tbody></table>");
            }
            html.AppendLine("</div>");

            html.AppendLine("<div class=\"goal-panel\">");
            html.AppendLine("<h3>Recorded proof</h3>");
            html.AppendLine("<p class=\"section-note\">Latest execution, handoff, process, or verification proof recorded for each task.</p>");
            html.AppendLine($"<p>Execution: {evidence.TasksWithExecution} &middot; Dispatch: {evidence.TasksWithDispatch} &middot; Process: {evidence.TasksWithProcess} (running {evidence.RunningProcesses}) &middot; Verification: {evidence.TasksWithVerification} (passed {evidence.PassedVerifications}, failed {evidence.FailedVerifications}) &middot; Pending input: {evidence.PendingHumanInputCount}</p>");
            html.AppendLine("<table><thead><tr><th>#</th><th>Evidence</th><th>Message</th></tr></thead><tbody>");
            foreach (var item in evidence.Tasks)
            {
                html.AppendLine("<tr>");
                html.AppendLine($"<td>{GetTaskDisplayNumber(goal, item.TaskId)}</td><td>{Encode(Display(item.LatestEvidence))}<br><span class=\"meta\">{item.Role} &middot; {Encode(Display(item.TaskStatus))}</span></td><td>{Encode(item.Message)}</td>");
                html.AppendLine("</tr>");
            }
            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");

            html.AppendLine("<div class=\"goal-panel goal-panel-wide\">");
            html.AppendLine("<h3>Task readiness</h3>");
            html.AppendLine("<p class=\"section-note\">Why each task can run, needs verification, is blocked, or is already verified.</p>");
            var stages = kernel.BuildStageReadinessReport(goal.Id);
            html.AppendLine($"<p class=\"{(stages.IsReadyForAcceptance ? "ok" : "bad")}\">Ready for acceptance: {stages.IsReadyForAcceptance} &middot; Verified: {stages.VerifiedStages}/{stages.TotalStages} &middot; Open: {stages.OpenStages} &middot; Blocked: {stages.BlockedStages}</p>");
            html.AppendLine("<table><thead><tr><th>#</th><th>Stage</th><th>Status</th><th>Action</th><th>Command</th></tr></thead><tbody>");
            foreach (var stage in stages.Stages)
            {
                var taskNumber = GetTaskDisplayNumber(goal, stage.TaskId);
                html.AppendLine("<tr>");
                html.AppendLine($"<td>{taskNumber}</td><td>{stage.Stage}<br><span class=\"meta\">{Encode(stage.Description)}</span></td><td>{Encode(Display(stage.StageStatus))}<br><span class=\"meta\">task {Encode(Display(stage.TaskStatus))} &middot; gate {Encode(Display(stage.VerificationStatus))} &middot; evidence {Encode(Display(stage.LatestEvidence))}</span></td><td>{Encode(stage.SuggestedAction)}<br><span class=\"meta\">{Encode(stage.Message)}</span></td><td><code>{Encode(BuildStageSuggestedCommand(taskNumber, stage))}</code></td>");
                html.AppendLine("</tr>");
            }
            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");

            html.AppendLine("<div class=\"goal-panel\">");
            html.AppendLine("<h3>Verification status</h3>");
            html.AppendLine("<p class=\"section-note\">Whether each task has enough verification proof to count toward goal completion.</p>");
            html.AppendLine($"<p class=\"{(verificationGate.IsSatisfied ? "ok" : "bad")}\">Satisfied: {verificationGate.IsSatisfied}</p>");
            html.AppendLine("<table><thead><tr><th>#</th><th>Gate</th><th>Message</th></tr></thead><tbody>");
            foreach (var gate in verificationGate.Tasks)
            {
                html.AppendLine("<tr>");
                html.AppendLine($"<td>{GetTaskDisplayNumber(goal, gate.TaskId)}</td><td>{Encode(Display(gate.GateStatus))}</td><td>{Encode(gate.Message)}</td>");
                html.AppendLine("</tr>");
            }
            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");

            html.AppendLine("<div class=\"goal-panel\">");
            html.AppendLine("<h3>Verification to-do</h3>");
            html.AppendLine("<p class=\"section-note\">Verification work still needed before the goal can complete.</p>");
            var verificationWorklist = kernel.BuildVerificationWorklist(goal.Id);
            html.AppendLine($"<p class=\"{(verificationWorklist.OpenCount == 0 ? "ok" : "bad")}\">Open: {verificationWorklist.OpenCount}</p>");
            html.AppendLine("<table><thead><tr><th>#</th><th>Gate</th><th>Action</th><th>Command</th></tr></thead><tbody>");
            if (verificationWorklist.Items.Count == 0)
            {
                html.AppendLine("<tr><td colspan=\"4\">none</td></tr>");
            }
            else
            {
                foreach (var item in verificationWorklist.Items)
                {
                    var taskNumber = GetTaskDisplayNumber(goal, item.TaskId);
                    html.AppendLine("<tr>");
                    html.AppendLine($"<td>{taskNumber}</td><td>{Encode(Display(item.GateStatus))}<br><span class=\"meta\">{Encode(item.Message)}</span></td><td>{Encode(item.SuggestedAction)}</td><td><code>{Encode(BuildVerificationSuggestedCommand(taskNumber, item.GateStatus))}</code></td>");
                    html.AppendLine("</tr>");
                }
            }
            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");
            html.AppendLine("</div>");

            html.AppendLine("<div class=\"goal-panel goal-panel-wide tasks-panel\">");
            html.AppendLine("<h3>Tasks</h3>");
            html.AppendLine("<p class=\"section-note\">The work items that make up the goal. Each task has a role, status, verification plan, and latest proof.</p>");
            html.AppendLine("<table class=\"tasks-table\"><thead><tr><th>#</th><th>Role</th><th>Status</th><th>Gate</th><th>Description</th><th>Verification Plan</th><th>Latest Evidence</th></tr></thead><tbody>");
            for (var index = 0; index < goal.Tasks.Count; index++)
            {
                var task = goal.Tasks[index];
                var gate = verificationGate.Tasks.Single(item => item.TaskId == task.Id);
                html.AppendLine("<tr class=\"task-summary-row\">");
                html.AppendLine($"<td>{index + 1}</td><td>{task.RequiredRole}</td><td>{Encode(Display(task.Status))}</td><td>{RenderTaskGate(gate)}</td><td>{Encode(task.Description)}</td><td>{RenderVerificationPlan(task)}</td>");
                html.AppendLine($"<td>{RenderEvidence(task)}</td>");
                if (options.EnableOperatorControls)
                {
                    html.AppendLine("</tr>");
                    html.AppendLine("<tr class=\"task-action-row\">");
                    html.AppendLine($"<td colspan=\"7\">{RenderTaskActions(goal, task)}</td>");
                }

                html.AppendLine("</tr>");
            }
            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");

            html.AppendLine("</section>");
        }

        html.AppendLine("</main>");
        if (options.EnableOperatorControls)
        {
            RenderOperatorControlsScript(html);
        }

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static void RenderSubscriptionRetryQueue(StringBuilder html, Goal goal)
    {
        var now = DateTimeOffset.UtcNow;
        var deferredTasks = goal.Tasks
            .Select(task => new
            {
                Task = task,
                IsDeferred = DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out var retryAfter),
                RetryAfter = retryAfter
            })
            .Where(item => item.IsDeferred)
            .OrderBy(item => item.RetryAfter)
            .ToList();

        if (deferredTasks.Count == 0)
        {
            return;
        }

        html.AppendLine("<div class=\"goal-panel goal-panel-wide subscription-retry-queue\">");
        html.AppendLine("<h3>Subscription retry queue</h3>");
        html.AppendLine("<p class=\"section-note\">Subscription handoff is paused until these provider retry windows pass.</p>");
        html.AppendLine("<table><thead><tr><th>#</th><th>Role</th><th>Retry after</th><th>Wait</th><th>Task</th></tr></thead><tbody>");
        foreach (var item in deferredTasks)
        {
            var delay = item.RetryAfter - now;
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{GetTaskDisplayNumber(goal, item.Task.Id)}</td><td>{item.Task.RequiredRole}</td><td><code>{Encode(item.RetryAfter.ToString("u"))}</code></td><td>{Encode(FormatRetryDelay(delay))}</td><td>{Encode(item.Task.Description)}</td>");
            html.AppendLine("</tr>");
        }

        html.AppendLine("</tbody></table>");
        html.AppendLine("</div>");
    }

    private static string FormatRetryDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return "ready now";
        }

        if (delay.TotalHours >= 1)
        {
            return $"{(int)delay.TotalHours}h {delay.Minutes}m";
        }

        return delay.TotalMinutes >= 1
            ? $"{(int)Math.Ceiling(delay.TotalMinutes)}m"
            : $"{(int)Math.Ceiling(delay.TotalSeconds)}s";
    }

    private static void RenderGoalArchiveControl(StringBuilder html, IReadOnlyList<Goal> goals, string? focusGoalPrefix)
    {
        html.AppendLine("<section class=\"goal-archive-control\">");
        html.AppendLine("<h2>Goal Archive</h2>");
        html.AppendLine("<p class=\"section-note\">Focus an older goal without loading every full goal card.</p>");
        html.AppendLine("<form class=\"controls compact\" method=\"get\" action=\"/\">");
        html.AppendLine("<div class=\"field\"><label for=\"goal-archive-focus\">Goal prefix</label><input id=\"goal-archive-focus\" class=\"wide\" name=\"goal\" list=\"goal-archive-options\" placeholder=\"goal prefix\" value=\"" + Encode(focusGoalPrefix ?? string.Empty) + "\"></div>");
        html.AppendLine("<datalist id=\"goal-archive-options\">");
        foreach (var goal in goals)
        {
            var prefix = goal.Id.Value[..8];
            html.AppendLine($"<option value=\"{Encode(prefix)}\">{Encode(goal.Objective)}</option>");
        }

        html.AppendLine("</datalist>");
        html.AppendLine("<button type=\"submit\">Focus Goal</button>");
        html.AppendLine("</form>");
        html.AppendLine("<div class=\"linkbar\">");
        foreach (var goal in goals.Take(16))
        {
            var prefix = goal.Id.Value[..8];
            html.AppendLine($"<a href=\"/?goal={Encode(prefix)}\">{Encode(prefix)}</a>");
        }

        html.AppendLine("</div>");
        html.AppendLine("</section>");
    }

    private static List<Goal> GetDisplayedGoals(IReadOnlyList<Goal> goals, DashboardRenderOptions options)
    {
        if (!options.EnableOperatorControls)
        {
            return goals.ToList();
        }

        var displayed = goals.Take(8).ToList();
        var focus = options.FocusGoalPrefix?.Trim();
        if (string.IsNullOrWhiteSpace(focus))
        {
            return displayed;
        }

        var focused = goals.FirstOrDefault(goal => goal.Id.Value.StartsWith(focus, StringComparison.OrdinalIgnoreCase));
        if (focused is null || displayed.Any(goal => goal.Id == focused.Id))
        {
            return displayed;
        }

        displayed.Insert(0, focused);
        return displayed;
    }

    private static bool IsManualOnlyCompletion(GoalEvidenceSummary evidence) =>
        evidence.TasksWithVerification > 0 &&
        evidence.TasksWithExecution == 0 &&
        evidence.TasksWithDispatch == 0 &&
        evidence.TasksWithProcess == 0;

}

