using System.Net;
using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public enum DashboardView { Ops, Goal, Config, System }

public sealed record DashboardRenderOptions(
    int? AutoRefreshSeconds = null,
    bool EnableOperatorControls = false,
    OrchestratorHealthReport? HealthReport = null,
    DashboardWorkspaceContext? Workspace = null,
    IReadOnlyList<DashboardContinuationStatusDto>? ContinuationWatches = null,
    string? FocusGoalPrefix = null,
    DashboardView View = DashboardView.Ops,
    IReadOnlyList<AgentDefinition>? AgentDefinitions = null,
    WorkerProfileCatalog? WorkerProfiles = null,
    OperatorInboxReportDto? OperatorInbox = null,
    IReadOnlyList<TaskDurationStatsDto>? TaskDurationStats = null,
    IReadOnlyList<string>? FocusGoalChangedFiles = null);

public sealed record DashboardWorkspaceContext(
    string RootDirectory,
    string SqliteStatePath,
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
        string sqliteStatePath,
        string executionDirectory,
        string promptDirectory,
        string logDirectory,
        string workerProfilePath,
        string agentCatalogPath,
        int dashboardProcessId)
        : this(
            rootDirectory,
            sqliteStatePath,
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
        var viewTitle = options.View switch
        {
            DashboardView.Config => "Configuration",
            DashboardView.System => "System",
            DashboardView.Goal => "Goal Detail",
            _ => "MCG Agent Orchestrator"
        };
        html.AppendLine($"<title>{Encode(viewTitle)} — MCG Agent Orchestrator</title>");
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
        RenderNavBar(html, options, displayedGoals);
        var refreshAttribute = options.AutoRefreshSeconds is > 0
            ? $" data-refresh-seconds=\"{options.AutoRefreshSeconds.Value}\""
            : string.Empty;
        var monitorStreamAttribute = options.EnableOperatorControls &&
            options.View == DashboardView.Goal &&
            !string.IsNullOrWhiteSpace(options.FocusGoalPrefix)
                ? $" data-monitor-stream=\"/api/goals/{Encode(options.FocusGoalPrefix)}/events/stream\""
                : string.Empty;
        html.AppendLine($"<main id=\"dashboard-content\"{refreshAttribute}{monitorStreamAttribute}>");
        if (!string.IsNullOrWhiteSpace(monitorStreamAttribute))
        {
            html.AppendLine("<section class=\"live-monitor\" data-live-monitor hidden>");
            html.AppendLine("<h2>Live Goal State</h2>");
            html.AppendLine("<div class=\"live-monitor-grid\">");
            html.AppendLine("<span data-live-status>waiting</span>");
            html.AppendLine("<span data-live-tasks>tasks pending</span>");
            html.AppendLine("<span data-live-inbox>inbox 0</span>");
            html.AppendLine("<span data-live-capacity>capacity unknown</span>");
            html.AppendLine("</div>");
            html.AppendLine("</section>");
        }

        switch (options.View)
        {
            case DashboardView.Config:
                RenderConfigView(html, options);
                break;
            case DashboardView.System:
                RenderSystemView(html, options);
                break;
            case DashboardView.Goal:
                RenderGoalDetailView(html, kernel, goals, displayedGoals, options);
                break;
            default:
                RenderOpsView(html, kernel, goals, displayedGoals, options);
                break;
        }

        html.AppendLine("</main>");
        if (options.EnableOperatorControls)
        {
            RenderOperatorControlsScript(html);
        }

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static void RenderNavBar(StringBuilder html, DashboardRenderOptions options, IReadOnlyList<Goal> displayedGoals)
    {
        var active = options.View;
        html.AppendLine("<nav class=\"dashboard-nav\">");
        html.AppendLine($"<a href=\"/\"{(active == DashboardView.Ops ? " class=\"active\"" : "")}>Operations</a>");
        html.AppendLine($"<a href=\"/config\"{(active == DashboardView.Config ? " class=\"active\"" : "")}>Configuration</a>");
        html.AppendLine($"<a href=\"/system\"{(active == DashboardView.System ? " class=\"active\"" : "")}>System</a>");
        if (!string.IsNullOrWhiteSpace(options.FocusGoalPrefix))
        {
            html.AppendLine($"<a href=\"/goal/{Encode(options.FocusGoalPrefix)}\"{(active == DashboardView.Goal ? " class=\"active\"" : "")}>Goal: {Encode(options.FocusGoalPrefix)}</a>");
        }
        html.AppendLine("</nav>");
    }

    private static void RenderConfigView(StringBuilder html, DashboardRenderOptions options)
    {
        if (options.HealthReport is not null)
        {
            RenderHealthReport(html, options.HealthReport, options.EnableOperatorControls);
        }
        else
        {
            html.AppendLine("<section><h2>Configuration</h2><p class=\"meta\">Health report is not available.</p></section>");
        }
    }

    private static void RenderSystemView(StringBuilder html, DashboardRenderOptions options)
    {
        if (options.EnableOperatorControls)
        {
            RenderSystemDiagnostics(html, options.Workspace, options.ContinuationWatches ?? []);
            RenderTaskDurationStats(html, options.TaskDurationStats ?? []);
        }
        else if (options.Workspace is not null)
        {
            RenderReadOnlyHostedContext(html, options.Workspace);
            RenderTaskDurationStats(html, options.TaskDurationStats ?? []);
        }
        else
        {
            html.AppendLine("<section><h2>System</h2><p class=\"meta\">No workspace context available.</p></section>");
        }
    }

    private static void RenderTaskDurationStats(StringBuilder html, IReadOnlyList<TaskDurationStatsDto> records)
    {
        html.AppendLine("<section>");
        html.AppendLine("<h2>Task Duration Estimates</h2>");
        html.AppendLine("<div class=\"linkbar\"><a href=\"/api/system/task-durations\" target=\"_blank\" rel=\"noreferrer\">Open duration stats JSON</a> <a href=\"/api/system/task-durations?byModel=true\" target=\"_blank\" rel=\"noreferrer\">Slice by model</a></div>");
        if (records.Count == 0)
        {
            html.AppendLine("<p class=\"meta\">No dispatch timing history found.</p>");
            html.AppendLine("</section>");
            return;
        }

        html.AppendLine("<table><thead><tr><th>Scope</th><th>Tasks</th><th>Attempts</th><th>Legit median</th><th>P90</th><th>Overhead median</th><th>Failure rate</th></tr></thead><tbody>");
        foreach (var record in records)
        {
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{Encode(record.Scope)}</td>");
            html.AppendLine($"<td>{record.TaskCount}</td>");
            html.AppendLine($"<td>{record.AttemptCount}</td>");
            html.AppendLine($"<td>{Encode(record.MedianLegitimateRuntime)}</td>");
            html.AppendLine($"<td>{Encode(record.P90LegitimateRuntime)}</td>");
            html.AppendLine($"<td>{Encode(record.MedianFailureInterventionOverhead)}</td>");
            html.AppendLine($"<td>{record.FailureRate:P0}</td>");
            html.AppendLine("</tr>");
        }

        html.AppendLine("</tbody></table>");
        html.AppendLine($"<p class=\"meta\">Buckets need at least {TaskDurationReport.MinSamplesForPublishedStats} samples before runtime estimates are published.</p>");
        html.AppendLine("</section>");
    }

    private static void RenderOpsView(
        StringBuilder html,
        AgentOrchestratorKernel kernel,
        List<Goal> goals,
        List<Goal> displayedGoals,
        DashboardRenderOptions options)
    {
        if (options.EnableOperatorControls)
        {
            RenderCreateGoalForm(html);
        }

        if (goals.Count == 0)
        {
            html.AppendLine(options.EnableOperatorControls
                ? "<section><h2>No goals</h2><p class=\"meta\">No active goals yet.</p></section>"
                : "<section><h2>No goals</h2><p>Create a goal from the CLI to populate this dashboard.</p></section>");
            return;
        }

        if (options.EnableOperatorControls && displayedGoals.Count < goals.Count)
        {
            var focusNote = string.IsNullOrWhiteSpace(options.FocusGoalPrefix)
                ? string.Empty
                : $" Focused goal filter: <code>{Encode(options.FocusGoalPrefix)}</code>.";
            html.AppendLine($"<section><p class=\"meta\">Showing {displayedGoals.Count} of {goals.Count} goals.{focusNote}</p></section>");
        }

        RenderGlobalAttentionSummary(html, kernel, displayedGoals);
        RenderOperatorInbox(html, options);

        foreach (var goal in displayedGoals)
        {
            var monitor = kernel.BuildMonitor(goal.Id);
            var verificationGate = kernel.BuildVerificationGate(goal.Id);
            var evidence = kernel.BuildGoalEvidenceSummary(goal.Id);
            var goalPrefix = goal.Id.Value[..8];
            html.AppendLine("<section class=\"goal-card\">");
            RenderGoalHeader(html, goal, monitor, verificationGate, evidence, options);
            RenderOperatorDisposition(
                html,
                ResolveOperatorDisposition(goal, monitor, verificationGate, options));

            // Attention items — always visible in ops
            if (monitor.AttentionItems.Count > 0)
            {
                html.AppendLine("<div class=\"goal-panel\">");
                html.AppendLine("<h3>Needs attention</h3>");
                foreach (var item in monitor.AttentionItems)
                {
                    var task = item.TaskId is null ? "goal" : item.TaskId.Value[..8];
                    html.AppendLine($"<div class=\"attention\"><strong>{Encode(Display(item.Kind))}</strong> <span class=\"meta\">{Encode(task)}</span><br>{Encode(OutputTextPreview.CreateTimeline(item.Message).Text)}</div>");
                }
                html.AppendLine("</div>");
            }

            // Next steps — always visible in ops
            var nextActions = kernel.BuildNextActions(goal.Id);
            if (nextActions.Items.Count > 0)
            {
                html.AppendLine("<div class=\"goal-panel\">");
                html.AppendLine("<h3>Recommended next steps</h3>");
                var nextActionControlHeader = options.EnableOperatorControls ? "<th>Control</th>" : string.Empty;
                html.AppendLine($"<table><thead><tr><th>Priority</th><th>Work item</th><th>Action</th><th>Suggested Command</th>{nextActionControlHeader}</tr></thead><tbody>");
                for (var index = 0; index < nextActions.Items.Count; index++)
                {
                    var item = nextActions.Items[index];
                    html.AppendLine("<tr>");
                    html.AppendLine($"<td>{index + 1}</td><td>{RenderWorkItemReference(goal, item.TaskId)}</td><td>{Encode(Display(item.Kind))}<br><span class=\"meta\">{Encode(OutputTextPreview.CreateTimeline(item.Message).Text)}</span></td><td><code>{Encode(BuildSuggestedCommand(goal, item, options.AgentDefinitions))}</code></td>");
                    if (options.EnableOperatorControls)
                    {
                        html.AppendLine($"<td>{RenderNextActionControl(goal, item, options)}</td>");
                    }
                    html.AppendLine("</tr>");
                }
                html.AppendLine("</tbody></table>");
                html.AppendLine("</div>");
            }

            RenderSubscriptionRetryQueue(html, goal);

            // Collapsed details: task status counts, work model
            html.AppendLine("<details>");
            html.AppendLine("<summary>Task status and work model</summary>");
            html.AppendLine("<div class=\"goal-overview-grid\">");
            html.AppendLine("<div class=\"goal-panel\">");
            html.AppendLine("<h3>Task status</h3>");
            html.AppendLine("<table><thead><tr><th>Status</th><th>Count</th></tr></thead><tbody>");
            foreach (var count in monitor.TaskStatusCounts)
            {
                html.AppendLine($"<tr><td>{Encode(Display(count.Status))}</td><td>{count.Count}</td></tr>");
            }
            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");
            html.AppendLine("</div>");
            RenderGoalWorkModel(html, goal, verificationGate);
            html.AppendLine("</details>");

            html.AppendLine("</section>");
        }

        // Pending input forms for focused goal
        if (options.EnableOperatorControls && !string.IsNullOrWhiteSpace(options.FocusGoalPrefix))
        {
            var focusedGoal = displayedGoals.FirstOrDefault(goal => goal.Id.Value.StartsWith(options.FocusGoalPrefix, StringComparison.OrdinalIgnoreCase));
            if (focusedGoal is not null)
            {
                RenderPendingInputForms(html, kernel, focusedGoal);
            }
        }

        if (options.EnableOperatorControls && goals.Count > 0)
        {
            RenderGoalArchiveControl(html, goals, options.FocusGoalPrefix);
        }

        if (options.EnableOperatorControls)
        {
            html.AppendLine("<div id=\"op-status\" class=\"statusline\" aria-live=\"polite\"></div>");
        }
    }

    private static void RenderGoalDetailView(
        StringBuilder html,
        AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> goals,
        IReadOnlyList<Goal> displayedGoals,
        DashboardRenderOptions options)
    {
        var goal = !string.IsNullOrWhiteSpace(options.FocusGoalPrefix)
            ? goals.FirstOrDefault(g => g.Id.Value.StartsWith(options.FocusGoalPrefix, StringComparison.OrdinalIgnoreCase))
            : displayedGoals.FirstOrDefault();
        if (goal is null)
        {
            html.AppendLine("<section><h2>Goal not found</h2><p class=\"meta\">No goal matches the given prefix.</p></section>");
            return;
        }

        RenderOperatorInbox(html, options);

        var monitor = kernel.BuildMonitor(goal.Id);
        var verificationGate = kernel.BuildVerificationGate(goal.Id);
        var evidence = kernel.BuildGoalEvidenceSummary(goal.Id);
        var goalPrefix = goal.Id.Value[..8];

        html.AppendLine("<section class=\"goal-card\">");
        RenderGoalHeader(html, goal, monitor, verificationGate, evidence, options);
        RenderOperatorDisposition(
            html,
            ResolveOperatorDisposition(goal, monitor, verificationGate, options));

        // Attention items and next steps — open
        html.AppendLine("<div class=\"goal-panel\">");
        html.AppendLine("<h3>Needs attention</h3>");
        if (monitor.AttentionItems.Count == 0)
        {
            html.AppendLine("<p class=\"ok\">No attention items.</p>");
        }
        else
        {
            foreach (var item in monitor.AttentionItems)
            {
                var task = item.TaskId is null ? "goal" : item.TaskId.Value[..8];
                html.AppendLine($"<div class=\"attention\"><strong>{Encode(Display(item.Kind))}</strong> <span class=\"meta\">{Encode(task)}</span><br>{Encode(OutputTextPreview.CreateTimeline(item.Message).Text)}</div>");
            }
        }
        html.AppendLine("</div>");

        html.AppendLine("<div class=\"goal-work-grid\">");
        // Human decisions
        html.AppendLine("<div class=\"goal-panel\">");
        html.AppendLine("<h3>Human decisions</h3>");
        var humanInputWorklist = kernel.BuildHumanInputWorklist(goal.Id);
        html.AppendLine($"<p class=\"{(humanInputWorklist.OpenCount == 0 ? "ok" : "bad")}\">Open: {humanInputWorklist.OpenCount}</p>");
        html.AppendLine("<table><thead><tr><th>Wait</th><th>Work item</th><th>Question</th><th>Policy</th><th>Command</th></tr></thead><tbody>");
        if (humanInputWorklist.Items.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"5\">none</td></tr>");
        }
        else
        {
            foreach (var item in humanInputWorklist.Items)
            {
                var answerLink = options.EnableOperatorControls
                    ? $"<br><a class=\"button-link\" href=\"#{Encode(BuildHumanInputFormAnchor(item.RequestId))}\">Answer</a>"
                    : string.Empty;
                var policy = $"auto-defaultable={item.IsAutoDefaultable}; dismissible={item.IsDismissible}; answer-required={item.IsAnswerRequired}; externally-blocked={item.IsExternallyBlocked}; age={item.AgeSeconds}s";
                html.AppendLine("<tr>");
                html.AppendLine($"<td>{Encode(item.RequestId.Value[..8])}<br><span class=\"meta\">{Encode(item.Kind.ToString())}</span></td><td>{RenderWorkItemReference(goal, item.TaskId, item.Role, item.TaskStatus, item.Description)}</td><td>{Encode(OutputTextPreview.CreateSummary(item.Question).Text)}<br><span class=\"meta\">{Encode(OutputTextPreview.CreateTimeline(item.SuggestedAction).Text)}</span></td><td><span class=\"meta\">{Encode(policy)}</span></td><td><code>{Encode(item.ResumeCommand)}</code>{answerLink}</td>");
                html.AppendLine("</tr>");
            }
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</div>");

        // Next steps
        html.AppendLine("<div class=\"goal-panel\">");
        html.AppendLine("<h3>Recommended next steps</h3>");
        var nextActionControlHeader = options.EnableOperatorControls ? "<th>Control</th>" : string.Empty;
        html.AppendLine($"<table><thead><tr><th>Priority</th><th>Work item</th><th>Action</th><th>Suggested Command</th>{nextActionControlHeader}</tr></thead><tbody>");
        var nextActions = kernel.BuildNextActions(goal.Id);
        for (var index = 0; index < nextActions.Items.Count; index++)
        {
            var item = nextActions.Items[index];
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{index + 1}</td><td>{RenderWorkItemReference(goal, item.TaskId)}</td><td>{Encode(Display(item.Kind))}<br><span class=\"meta\">{Encode(OutputTextPreview.CreateTimeline(item.Message).Text)}</span></td><td><code>{Encode(BuildSuggestedCommand(goal, item, options.AgentDefinitions))}</code></td>");
            if (options.EnableOperatorControls)
            {
                html.AppendLine($"<td>{RenderNextActionControl(goal, item, options)}</td>");
            }
            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</div>");
        html.AppendLine("</div>");

        // Pending input forms
        if (options.EnableOperatorControls)
        {
            RenderPendingInputForms(html, kernel, goal);
        }

        // Operator controls
        if (options.EnableOperatorControls)
        {
            RenderGoalOperatorControls(html, kernel, goal, options);
        }

        RenderSubscriptionRetryQueue(html, goal);
        RenderGoalWorkModel(html, goal, verificationGate);

        // Readiness: goal completion open, others in <details>
        html.AppendLine("<div class=\"goal-readiness-grid\">");
        var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
        html.AppendLine("<div class=\"goal-panel\">");
        html.AppendLine("<h3>Goal completion</h3>");
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
                int? taskNumber = blocker.TaskId is null ? null : TaskDisplayNumber.Resolve(goal, blocker.TaskId);
                var scope = taskNumber is null ? "goal" : $"task {taskNumber}";
                html.AppendLine("<tr>");
                html.AppendLine($"<td>{Encode(scope)}</td><td>{Encode(Display(blocker.Kind))}<br><span class=\"meta\">{Encode(OutputTextPreview.CreateTimeline(blocker.Message).Text)}</span></td><td>{Encode(OutputTextPreview.CreateTimeline(blocker.SuggestedAction).Text)}</td><td><code>{Encode(ConsoleViews.BuildAcceptanceSuggestedCommand(blocker, taskNumber))}</code></td>");
                html.AppendLine("</tr>");
            }
            html.AppendLine("</tbody></table>");
        }
        html.AppendLine("</div>");

        var verificationWorklist = kernel.BuildVerificationWorklist(goal.Id);
        var verificationTodoOpen = verificationWorklist.OpenCount > 0 ? " open" : string.Empty;
        html.AppendLine($"<details{verificationTodoOpen}>");
        html.AppendLine("<summary>Verification to-do</summary>");
        html.AppendLine("<div class=\"goal-panel\">");
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
                var taskNumber = TaskDisplayNumber.Resolve(goal, item.TaskId);
                html.AppendLine("<tr>");
                html.AppendLine($"<td>{taskNumber}</td><td>{Encode(Display(item.GateStatus))}<br><span class=\"meta\">{Encode(OutputTextPreview.CreateTimeline(item.Message).Text)}</span></td><td>{Encode(OutputTextPreview.CreateTimeline(item.SuggestedAction).Text)}</td><td><code>{Encode(ConsoleViews.BuildVerificationSuggestedCommand(taskNumber, item.GateStatus))}</code></td>");
                html.AppendLine("</tr>");
            }
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</div>");
        html.AppendLine("</details>");

        html.AppendLine("<details>");
        html.AppendLine("<summary>Recorded proof</summary>");
        html.AppendLine("<div class=\"goal-panel\">");
        html.AppendLine($"<p>Execution: {evidence.TasksWithExecution} &middot; Tokens: {FormatTokenUsage(evidence.InputTokens, evidence.OutputTokens)} &middot; Potentially paid: {FormatTokenUsage(evidence.PotentiallyPaidInputTokens, evidence.PotentiallyPaidOutputTokens)} &middot; Dispatch: {evidence.TasksWithDispatch} &middot; Process: {evidence.TasksWithProcess} (running {evidence.RunningProcesses}) &middot; Verification: {evidence.TasksWithVerification} (passed {evidence.PassedVerifications}, failed {evidence.FailedVerifications}) &middot; Pending input: {evidence.PendingHumanInputCount}</p>");
        if (evidence.ModelUsage.Count > 0)
        {
            html.AppendLine("<p>Model usage: " + string.Join(" &middot; ", evidence.ModelUsage.Select(RenderModelUsageSummary)) + "</p>");
        }
        if (evidence.DispatchModelUsage.Count > 0)
        {
            html.AppendLine("<p>Dispatch models: " + string.Join(" &middot; ", evidence.DispatchModelUsage.Select(RenderDispatchModelSummary)) + "</p>");
        }
        if (evidence.ModelFit.Count > 0)
        {
            html.AppendLine("<p>Model fit: " + string.Join(" &middot; ", evidence.ModelFit.Select(RenderModelFitSummary)) + "</p>");
        }
        html.AppendLine("<table><thead><tr><th>#</th><th>Evidence</th><th>Message</th></tr></thead><tbody>");
        foreach (var item in evidence.Tasks)
        {
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{TaskDisplayNumber.Resolve(goal, item.TaskId)}</td><td>{Encode(Display(item.LatestEvidence))}<br><span class=\"meta\">{item.Role} &middot; {Encode(Display(item.TaskStatus))}</span></td><td>{Encode(OutputTextPreview.CreateTimeline(item.Message).Text)}</td>");
            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</div>");
        html.AppendLine("</details>");

        html.AppendLine("<details>");
        html.AppendLine("<summary>Task readiness</summary>");
        html.AppendLine("<div class=\"goal-panel goal-panel-wide\">");
        var stages = kernel.BuildStageReadinessReport(goal.Id);
        html.AppendLine($"<p class=\"{(stages.IsReadyForAcceptance ? "ok" : "bad")}\">Ready for acceptance: {stages.IsReadyForAcceptance} &middot; Verified: {stages.VerifiedStages}/{stages.TotalStages} &middot; Open: {stages.OpenStages} &middot; Blocked: {stages.BlockedStages}</p>");
        html.AppendLine("<table><thead><tr><th>#</th><th>Stage</th><th>Status</th><th>Action</th><th>Command</th></tr></thead><tbody>");
        foreach (var stage in stages.Stages)
        {
            var taskNumber = TaskDisplayNumber.Resolve(goal, stage.TaskId);
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{taskNumber}</td><td>{stage.Stage}<br><span class=\"meta\">{Encode(OutputTextPreview.CreateSummary(stage.Description).Text)}</span></td><td>{Encode(Display(stage.StageStatus))}<br><span class=\"meta\">task {Encode(Display(stage.TaskStatus))} &middot; gate {Encode(Display(stage.VerificationStatus))} &middot; evidence {Encode(Display(stage.LatestEvidence))}</span></td><td>{Encode(OutputTextPreview.CreateTimeline(stage.SuggestedAction).Text)}<br><span class=\"meta\">{Encode(OutputTextPreview.CreateTimeline(stage.Message).Text)}</span></td><td><code>{Encode(BuildStageSuggestedCommand(goal, stage, options.AgentDefinitions))}</code></td>");
            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</div>");
        html.AppendLine("</details>");

        html.AppendLine("<details>");
        html.AppendLine("<summary>Verification status</summary>");
        html.AppendLine("<div class=\"goal-panel\">");
        html.AppendLine($"<p class=\"{(verificationGate.IsSatisfied ? "ok" : "bad")}\">Satisfied: {verificationGate.IsSatisfied}</p>");
        html.AppendLine("<table><thead><tr><th>#</th><th>Gate</th><th>Message</th></tr></thead><tbody>");
        foreach (var gate in verificationGate.Tasks)
        {
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{TaskDisplayNumber.Resolve(goal, gate.TaskId)}</td><td>{Encode(Display(gate.GateStatus))}</td><td>{Encode(OutputTextPreview.CreateTimeline(gate.Message).Text)}</td>");
            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</div>");
        html.AppendLine("</details>");
        html.AppendLine("</div>");

        // Tasks table
        html.AppendLine("<div class=\"goal-panel goal-panel-wide tasks-panel\">");
        html.AppendLine("<h3>Tasks</h3>");
        html.AppendLine("<table class=\"tasks-table\"><thead><tr><th>#</th><th>Role</th><th>Status</th><th>Gate</th><th>Description</th><th>Verification Plan</th><th>Latest Evidence</th></tr></thead><tbody>");
        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            var task = goal.Tasks[index];
            var gate = verificationGate.Tasks.Single(item => item.TaskId == task.Id);
            html.AppendLine("<tr class=\"task-summary-row\">");
            html.AppendLine($"<td>{index + 1}</td><td>{task.RequiredRole}</td><td>{Encode(Display(task.Status))}</td><td>{RenderTaskGate(gate)}</td><td>{Encode(OutputTextPreview.CreateSummary(task.Description).Text)}</td><td>{RenderVerificationPlan(task)}</td>");
            html.AppendLine($"<td>{RenderEvidence(task)}</td>");
            if (options.EnableOperatorControls)
            {
                html.AppendLine("</tr>");
                html.AppendLine("<tr class=\"task-action-row\">");
                html.AppendLine($"<td colspan=\"7\">{RenderTaskActions(kernel, goal, task, options)}</td>");
            }
            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</div>");

        html.AppendLine("</section>");

        if (options.EnableOperatorControls)
        {
            html.AppendLine("<div id=\"op-status\" class=\"statusline\" aria-live=\"polite\"></div>");
        }
    }

    private static void RenderGoalHeader(
        StringBuilder html,
        Goal goal,
        GoalMonitor monitor,
        GoalVerificationGate verificationGate,
        GoalEvidenceSummary evidence,
        DashboardRenderOptions options)
    {
        var goalPrefix = goal.Id.Value[..8];
        html.AppendLine("<div class=\"goal-header\">");
        html.AppendLine($"<h2><a href=\"/goal/{Encode(goalPrefix)}\">{Encode(goal.Objective)}</a></h2>");
        var goalJsonLink = options.Workspace is not null
            ? $" &middot; <a href=\"/api/goals/{Encode(goalPrefix)}\" target=\"_blank\" rel=\"noopener\">Goal JSON</a>"
            : string.Empty;
        html.AppendLine($"<div class=\"meta\">Goal {Encode(goal.Id.Value)} &middot; <span class=\"pill\">{Encode(Display(goal.Status))}</span> &middot; Last event {Encode(monitor.LastTimelineEventAt?.ToString("u") ?? "n/a")}{goalJsonLink}</div>");
        html.AppendLine("</div>");
        if (goal.Status == GoalStatus.Verified && verificationGate.IsSatisfied)
        {
            html.AppendLine("<div class=\"completion-banner\">");
            html.AppendLine("<strong>Goal verified</strong>");
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
    }

    private static void RenderGlobalAttentionSummary(
        StringBuilder html,
        AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> displayedGoals)
    {
        html.AppendLine("<section>");
        html.AppendLine("<h2>Goals</h2>");
        html.AppendLine("<table><thead><tr><th>Goal</th><th>Status</th><th>Tasks</th><th>Attention</th><th>Pending Input</th></tr></thead><tbody>");
        foreach (var goal in displayedGoals)
        {
            var monitor = kernel.BuildMonitor(goal.Id);
            var prefix = goal.Id.Value[..8];
            var attentionClass = monitor.AttentionItems.Count > 0 ? " class=\"bad\"" : "";
            var inputClass = monitor.PendingHumanInputCount > 0 ? " class=\"bad\"" : "";
            html.AppendLine("<tr>");
            html.AppendLine($"<td><a href=\"/goal/{Encode(prefix)}\">{Encode(TruncateObjective(goal.Objective))}</a></td>");
            html.AppendLine($"<td><span class=\"pill\">{Encode(Display(goal.Status))}</span></td>");
            html.AppendLine($"<td>{monitor.TotalTasks}</td>");
            html.AppendLine($"<td{attentionClass}>{monitor.AttentionItems.Count}</td>");
            html.AppendLine($"<td{inputClass}>{monitor.PendingHumanInputCount}</td>");
            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</section>");
    }

    private static void RenderOperatorInbox(StringBuilder html, DashboardRenderOptions options)
    {
        var inbox = options.OperatorInbox;
        if (inbox is null)
        {
            return;
        }

        html.AppendLine("<section>");
        html.AppendLine("<h2>Operator inbox</h2>");
        html.AppendLine($"<p class=\"meta\">Open: {inbox.OpenCount} &middot; acknowledged: {inbox.AcknowledgedCount}</p>");
        if (inbox.Items.Count == 0)
        {
            html.AppendLine("<p class=\"ok\">No operator decisions pending.</p>");
            html.AppendLine("</section>");
            return;
        }

        var ackHeader = options.EnableOperatorControls ? "<th>Ack</th>" : string.Empty;
        html.AppendLine($"<table><thead><tr><th>Severity</th><th>Goal</th><th>Work item</th><th>Decision</th><th>Suggested command</th>{ackHeader}</tr></thead><tbody>");
        foreach (var item in inbox.Items)
        {
            var severityClass = item.Severity == OperatorInboxSeverity.Blocker ? "bad" : item.Severity == OperatorInboxSeverity.Warning ? "warn" : "meta";
            var workItem = item.TaskNumber is null ? "goal" : $"task {item.TaskNumber}";
            var ackCell = string.Empty;
            if (options.EnableOperatorControls)
            {
                var ackUrl = $"/api/operator-inbox/ack?itemId={Uri.EscapeDataString(item.Id)}";
                if (!string.IsNullOrWhiteSpace(options.FocusGoalPrefix))
                {
                    ackUrl += $"&goal={Uri.EscapeDataString(options.FocusGoalPrefix)}";
                }

                ackCell = $"<td><form method=\"post\" action=\"{Encode(ackUrl)}\"><button type=\"submit\">Ack</button></form></td>";
            }

            html.AppendLine("<tr>");
            html.AppendLine($"<td><span class=\"{severityClass}\">{Encode(item.Severity.ToString())}</span><br><span class=\"meta\">{Encode(item.Kind.ToString())}</span></td>");
            html.AppendLine($"<td><a href=\"/goal/{Encode(item.GoalPrefix)}\">{Encode(item.GoalPrefix)}</a><br><span class=\"meta\">{Encode(OutputTextPreview.CreateSummary(item.Objective).Text)}</span></td>");
            html.AppendLine($"<td>{Encode(workItem)}<br><span class=\"meta\">{Encode(item.Id)}</span></td>");
            html.AppendLine($"<td><strong>{Encode(item.Title)}</strong><br>{Encode(OutputTextPreview.CreateTimeline(item.Message).Text)}<br><span class=\"meta\">{Encode(OutputTextPreview.CreateTimeline(item.Evidence).Text)}</span></td>");
            html.AppendLine($"<td><code>{Encode(item.SuggestedCommand)}</code><br><span class=\"meta\">{Encode(OutputTextPreview.CreateTimeline(item.SuggestedAction).Text)}</span></td>{ackCell}");
            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</section>");
    }

    private static void RenderPendingInputForms(StringBuilder html, AgentOrchestratorKernel kernel, Goal goal)
    {
        var pending = kernel.HumanInputRequests
            .Where(request => request.GoalId == goal.Id && !request.IsCompleted)
            .OrderBy(request => request.RequestedAt)
            .ToList();
        if (pending.Count == 0) return;
        html.AppendLine("<h3>Pending Input</h3>");
        foreach (var request in pending)
        {
            RenderPendingInputForm(html, goal, request);
        }
    }

    private static string TruncateObjective(string objective)
    {
        const int maxLength = 60;
        return objective.Length <= maxLength
            ? objective
            : string.Concat(objective.AsSpan(0, maxLength - 1), "...");
    }

    private static void RenderSubscriptionRetryQueue(StringBuilder html, Goal goal)
    {
        var now = DateTimeOffset.UtcNow;
        var deferredTasks = goal.Tasks
            .Select(task => new
            {
                Task = task,
                IsDeferred = DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out var retryAfter),
                RetryAfter = retryAfter,
                RecoverableLimitFailures = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task)
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
        html.AppendLine("<table><thead><tr><th>#</th><th>Role</th><th>Retry after</th><th>Wait</th><th>Failures</th><th>Task</th></tr></thead><tbody>");
        foreach (var item in deferredTasks)
        {
            var delay = item.RetryAfter - now;
            var failures = item.RecoverableLimitFailures == 1
                ? "1 limit failure"
                : $"{item.RecoverableLimitFailures} limit failures";
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{TaskDisplayNumber.Resolve(goal, item.Task.Id)}</td><td>{item.Task.RequiredRole}</td><td><code>{Encode(item.RetryAfter.ToString("u"))}</code></td><td>{Encode(FormatRetryDelay(delay))}</td><td>{Encode(failures)}</td><td>{Encode(item.Task.Description)}</td>");
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
        var displayed = goals
            .Where(IsActionableGoal)
            .Take(8)
            .ToList();
        if (displayed.Count < 8)
        {
            displayed.AddRange(goals
                .Where(goal => !IsActionableGoal(goal) && displayed.All(existing => existing.Id != goal.Id))
                .Take(8 - displayed.Count));
        }

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

    private static bool IsActionableGoal(Goal goal)
    {
        return goal.Status is not GoalStatus.Completed and not GoalStatus.Cancelled and not GoalStatus.Superseded;
    }

    private static bool IsManualOnlyCompletion(GoalEvidenceSummary evidence) =>
        evidence.TasksWithVerification > 0 &&
        evidence.TasksWithExecution == 0 &&
        evidence.TasksWithDispatch == 0 &&
        evidence.TasksWithProcess == 0;

    private static GoalOperatorDisposition ResolveOperatorDisposition(
        Goal goal,
        GoalMonitor monitor,
        GoalVerificationGate verificationGate,
        DashboardRenderOptions options)
    {
        var runEventStorePath = options.Workspace?.SqliteStatePath is { Length: > 0 } statePath
            ? Path.Combine(Path.GetDirectoryName(statePath) ?? string.Empty, "run-events.db")
            : null;
        return runEventStorePath is not null
            && ConductorOperatorDispositionSnapshots.TryReadLatestForGoal(runEventStorePath, goal) is { } conductorDisposition
                ? conductorDisposition
                : new GoalOperatorDispositionSurface().Evaluate(
                    goal,
                    monitor.PendingHumanInputCount,
                    verificationGate.IsSatisfied,
                    options.Workspace?.ExecutionDirectory);
    }

}
