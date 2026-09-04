using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static void PrintBoundedGoalDiagnostics(CliExecutionContext context)
{
    var goal = context.CurrentGoal!;
    var prefix = goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)];
    Console.WriteLine();
    Console.WriteLine($"Goal diagnostics {prefix} {goal.Status}: {OutputTextPreview.CreateSummary(goal.Objective).Text}");
    Console.WriteLine("Mode: bounded; skipped deep readiness, subscription prompt estimation, recovery planning, supervisor planning, inbox scan, and dispatch worktree git inspection.");

    var diagnosticsSweep = TerminalGoalSweep.Diagnose(context.Kernel, context.Workspace.ExecutionDirectory, goal.Id);
    ConsoleViews.PrintTerminalGoalSweep(diagnosticsSweep, includeRepairs: false);
    TerminalGoalSweepAttention.Surface(context.Kernel, diagnosticsSweep, context.Workspace.OrchestratorDirectory, goal.Id);

    var verificationSatisfied = goal.Tasks.Count > 0 && goal.Tasks.All(task => task.LastVerification?.Succeeded == true);
    var dispatchSurface = new DispatchStateSurface(inspectWorktree: false);
    var dispositionSurface = new GoalOperatorDispositionSurface(dispatchSurface: dispatchSurface);
    var processSnapshot = ProcessCommandLines.SnapshotOperation();
    var disposition = dispositionSurface.Evaluate(
        goal,
        pendingHumanInputCount: context.Kernel.BuildHumanInputWorklist(goal.Id).OpenCount,
        verificationSatisfied,
        commandLineSnapshot: processSnapshot);
    ConsoleViews.PrintOperatorDisposition(disposition);

    Console.WriteLine("Dispatches:");
    var dispatches = disposition.Dispatches
        .Where(dispatch => dispatch.DispatchState is not null || dispatch.State is not OperatorDispositionState.Idle)
        .ToList();
    if (dispatches.Count == 0)
    {
        Console.WriteLine("  none");
    }
    else
    {
        foreach (var dispatch in dispatches)
        {
            var task = goal.Tasks.First(candidate => candidate.Id == dispatch.TaskId);
            Console.WriteLine($"  Task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} {dispatch.Role} status={dispatch.TaskStatus} state={dispatch.State} command='{dispatch.NextSafeCommand}' reason='{OutputTextPreview.CreateTimeline(dispatch.Reason).Text}'");
            if (dispatch.DispatchState is { } dispatchState)
            {
                ConsoleViews.PrintDispatchState(dispatchState, "    ");
            }
        }
    }

    var actions = context.Kernel.BuildNextActions(goal.Id);
    Console.WriteLine("Next actions:");
    if (actions.Items.Count == 0)
    {
        Console.WriteLine("  none");
    }
    else
    {
        foreach (var item in actions.Items.Take(3))
        {
            Console.WriteLine($"  {item.Kind}: {OutputTextPreview.CreateTimeline(item.Message).Text}");
        }
    }

    Console.WriteLine("Deeper commands:");
    Console.WriteLine($"  readiness {prefix}");
    Console.WriteLine($"  evidence {prefix}");
    Console.WriteLine($"  stages {prefix}");
    Console.WriteLine($"  gates {prefix}");
    Console.WriteLine($"  subscription-plan {prefix}");
    Console.WriteLine($"  model-outcomes");
    Console.WriteLine($"  dispatch-value [--since <yyyy-mm-dd>]");
    Console.WriteLine($"  loop-health");
    Console.WriteLine($"  failure-triage {prefix}");
    Console.WriteLine($"  goal-recovery {prefix}");
    Console.WriteLine($"  operator-inbox {prefix}");
    Console.WriteLine();
}

private static void PrintNextFullDetail(CliExecutionContext context, AutonomyPolicy policy)
{
    var goal = context.CurrentGoal!;
    ConsoleViews.PrintGoal(goal);
    ConsoleViews.PrintMonitor(context.Kernel.BuildMonitor(goal.Id));
    ConsoleViews.PrintGoalReadinessPreflight(GoalReadinessPreflight.Build(
        goal,
        context.Agents,
        context.Workspace.ExecutionDirectory,
        context.WorkerProfiles,
        context.Worktrees.TryResolve));
    ConsoleViews.PrintEvidenceSummary(goal, context.Kernel.BuildGoalEvidenceSummary(goal.Id));
    ConsoleViews.PrintStageReadinessReport(goal, context.Kernel.BuildStageReadinessReport(goal.Id), context.Agents);
    ConsoleViews.PrintVerificationGate(goal, context.Kernel.BuildVerificationGate(goal.Id));
    ConsoleViews.PrintVerificationWorklist(goal, context.Kernel.BuildVerificationWorklist(goal.Id));
    ConsoleViews.PrintHumanInputWorklist(goal, context.Kernel.BuildHumanInputWorklist(goal.Id));
    ConsoleViews.PrintSubscriptionPlan(SubscriptionPlanBuilder.Build(
        goal,
        context.Agents,
        context.WorkerProfiles,
        task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(context.Kernel, goal, task, context.Agents)));
    ConsoleViews.PrintModelOutcomeScorecard(context.Kernel.BuildModelOutcomeScorecard());
    ConsoleViews.PrintLoopHealthReport(context.Kernel.BuildLoopHealthReport(null));
    ConsoleViews.PrintFailureTriageReport(FailureTriagePlanner.Build(context.Kernel, goal, context.Agents, context.Workspace.ExecutionDirectory, policy));
    ConsoleViews.PrintGoalRecoveryReport(GoalRecoveryPlanner.Build(context.Kernel, goal, context.Workspace.ExecutionDirectory));
    ConsoleViews.PrintGoalSupervisorPlan(GoalSupervisor.Build(context.Kernel, goal, context.Agents, context.Workspace.ExecutionDirectory, policy));
    ConsoleViews.PrintOperatorInbox(OperatorInbox.Build(context.Kernel, context.Agents, context.WorkerProfiles, context.Workspace, goal.Id.Value[..8], includeAcknowledged: false));
}

internal static string? ResolveGoalFriendlyLabel(Goal goal, string backlogStorePath)
{
    if (string.IsNullOrWhiteSpace(goal.SourceBacklogItemId) ||
        string.IsNullOrWhiteSpace(backlogStorePath) ||
        !File.Exists(backlogStorePath))
    {
        return null;
    }

    try
    {
        var item = new BacklogStore(backlogStorePath)
            .GetByExactIdAsync(goal.SourceBacklogItemId)
            .GetAwaiter()
            .GetResult();
        return string.IsNullOrWhiteSpace(item?.Title) ? null : item.Title;
    }
    catch
    {
        return null;
    }
}

private static IReadOnlyDictionary<GoalId, GoalTimingReportContext> BuildGoalTimingContexts(CliExecutionContext context)
{
    var asOf = DateTimeOffset.UtcNow;
    var journals = GoalOperationJournal.ReadAll(
        context.Workspace.ExecutionDirectory,
        context.Kernel.Goals.Select(goal => goal.Id));
    return context.Kernel.Goals.ToDictionary(
        goal => goal.Id,
        goal => BuildGoalTimingContext(
            context,
            goal,
            journals.TryGetValue(goal.Id, out var journal) ? journal : null,
            asOf));
}

private static GoalTimingReportContext BuildGoalTimingContext(
    CliExecutionContext context,
    Goal goal,
    GoalOperationJournalSummary? journal = null,
    DateTimeOffset? asOf = null)
{
    asOf ??= DateTimeOffset.UtcNow;
    journal ??= GoalOperationJournal.Read(context.Workspace.ExecutionDirectory, goal.Id);
    var backlogIntent = ResolveBacklogIntent(goal, context.Workspace.BacklogStorePath);
    var gateSpans = BuildGoalTimingGateSpans(journal);
    var landing = ResolveGoalTimingLanding(context.Workspace.ExecutionDirectory, goal, journal);
    var end = ResolveGoalTimingEnd(goal, journal, landing, asOf.Value);
    return new GoalTimingReportContext(
        BacklogIntentAt: backlogIntent.IntentAt,
        LandedAt: landing.LandedAt,
        LandingSource: landing.Source,
        GateSpans: gateSpans,
        AsOf: asOf,
        BacklogIntentSource: backlogIntent.Source,
        GoalE2EEndedAt: end.EndedAt,
        GoalE2EEndSource: end.Source);
}

private static (DateTimeOffset? IntentAt, string? Source) ResolveBacklogIntent(Goal goal, string backlogStorePath)
{
    if (string.IsNullOrWhiteSpace(backlogStorePath) ||
        !File.Exists(backlogStorePath))
    {
        return (null, null);
    }

    try
    {
        var store = new BacklogStore(backlogStorePath);
        if (!string.IsNullOrWhiteSpace(goal.SourceBacklogItemId) &&
            TryResolveBacklogItemCreatedAt(store, goal.SourceBacklogItemId, out var linkedCreatedAt))
        {
            return (linkedCreatedAt, "linked-backlog-created-at");
        }

        foreach (Match match in BacklogObjectiveReferenceRegex.Matches(goal.Objective))
        {
            if (TryResolveBacklogItemCreatedAt(store, match.Groups[1].Value, out var objectiveCreatedAt))
            {
                return (objectiveCreatedAt, "objective-backlog-created-at");
            }
        }
    }
    catch
    {
    }

    return (null, null);
}

private static bool TryResolveBacklogItemCreatedAt(
    BacklogStore store,
    string idOrPrefix,
    out DateTimeOffset createdAt)
{
    createdAt = default;
    if (string.IsNullOrWhiteSpace(idOrPrefix))
    {
        return false;
    }

    var item = store.GetByExactIdAsync(idOrPrefix).GetAwaiter().GetResult();
    if (item is null && idOrPrefix.Length >= 8)
    {
        item = store.GetByIdPrefixAsync(idOrPrefix).GetAwaiter().GetResult();
    }

    if (item is null)
    {
        return false;
    }

    createdAt = item.CreatedAt;
    return true;
}

private static IReadOnlyList<GoalTimingGateSpan> BuildGoalTimingGateSpans(GoalOperationJournalSummary journal)
{
    var spans = new List<GoalTimingGateSpan>();
    var entries = journal.Entries
        .Where(entry => IsAcceptanceTimingOperation(entry.Operation))
        .OrderBy(entry => entry.At)
        .ToList();
    for (var index = 0; index < entries.Count; index++)
    {
        var begin = entries[index];
        if (begin.Status != GoalOperationStatus.Begin)
        {
            continue;
        }

        var end = entries
            .Skip(index + 1)
            .FirstOrDefault(entry =>
                entry.Status != GoalOperationStatus.Begin &&
                entry.Operation.Equals(begin.Operation, StringComparison.OrdinalIgnoreCase));
        if (end is null)
        {
            continue;
        }

        spans.Add(new GoalTimingGateSpan(
            begin.At,
            end.At,
            string.IsNullOrWhiteSpace(end.AcceptanceOutcome) ? end.Status.ToString() : end.AcceptanceOutcome!));
    }

    return spans;
}

private static bool IsAcceptanceTimingOperation(string operation) =>
    operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
    operation.Equals("conductor:acceptance", StringComparison.OrdinalIgnoreCase);

private static (DateTimeOffset? LandedAt, string? Source) ResolveGoalTimingLanding(
    string executionDirectory,
    Goal goal,
    GoalOperationJournalSummary journal)
{
    var dispositionAt = journal.Entries
        .Where(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            entry.Operation.Equals(GoalOperationJournal.TerminalDispositionOperation, StringComparison.OrdinalIgnoreCase))
        .OrderBy(entry => entry.At)
        .Select(entry => (DateTimeOffset?)entry.At)
        .LastOrDefault();

    if (GoalOperationJournal.HasRetiredTerminalDisposition(journal))
    {
        return (null, null);
    }

    var conductorLandingAt = journal.Entries
        .Where(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            dispositionAt is null &&
            (entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase)))
        .OrderBy(entry => entry.At)
        .Select(entry => (DateTimeOffset?)entry.At)
        .LastOrDefault();
    if (conductorLandingAt is not null)
    {
        return (conductorLandingAt, "landing-journal");
    }

    var legacyAcceptanceLandingAt = journal.Entries
        .Where(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase))
        .OrderBy(entry => entry.At)
        .Select(entry => (DateTimeOffset?)entry.At)
        .LastOrDefault();
    if (legacyAcceptanceLandingAt is not null)
    {
        return (legacyAcceptanceLandingAt, "acceptance-journal");
    }

    if (TryResolveIntegrationCommitAuthoredAt(executionDirectory, goal, out var commitAuthoredAt))
    {
        return (commitAuthoredAt, "git-integration-commit");
    }

    if (dispositionAt is not null)
    {
        return (dispositionAt, "terminal-disposition");
    }

    return goal is { IsMetadataOnly: true, Status: GoalStatus.Completed, MetadataTerminatedAt: { } metadataTerminatedAt }
        ? (metadataTerminatedAt, "terminal-metadata")
        : (null, null);
}

private static (DateTimeOffset? EndedAt, string Source) ResolveGoalTimingEnd(
    Goal goal,
    GoalOperationJournalSummary journal,
    (DateTimeOffset? LandedAt, string? Source) landing,
    DateTimeOffset asOf)
{
    if (landing.LandedAt is { } landedAt)
    {
        return (landedAt, landing.Source ?? "landing-evidence");
    }

    var terminalDispositionAt = journal.Entries
        .Where(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            entry.Operation.Equals(GoalOperationJournal.TerminalDispositionOperation, StringComparison.OrdinalIgnoreCase))
        .OrderBy(entry => entry.At)
        .Select(entry => (DateTimeOffset?)entry.At)
        .LastOrDefault();
    if (terminalDispositionAt is not null)
    {
        var source = GoalOperationJournal.HasRetiredTerminalDisposition(journal)
            ? "retired-terminal-disposition"
            : "terminal-disposition";
        return (terminalDispositionAt, source);
    }

    if (goal.IsTerminal && goal.MetadataTerminatedAt is { } metadataTerminatedAt)
    {
        return (metadataTerminatedAt, "terminal-metadata");
    }

    if (goal.IsTerminal)
    {
        var terminalTimelineAt = goal.Timeline
            .OrderByDescending(entry => entry.OccurredAt)
            .Select(entry => (DateTimeOffset?)entry.OccurredAt)
            .FirstOrDefault();
        if (terminalTimelineAt is not null)
        {
            return (terminalTimelineAt, "terminal-timeline");
        }
    }

    return (asOf, "sample-time");
}

private static bool TryResolveIntegrationCommitAuthoredAt(
    string executionDirectory,
    Goal goal,
    out DateTimeOffset authoredAt)
{
    authoredAt = default;
    if (!Directory.Exists(executionDirectory))
    {
        return false;
    }

    var goalPrefix = goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)];
    var branchName = GoalWorktrees.BranchName(goal.Id);
    foreach (var pattern in new[] { $"Integrate {branchName}", $"Integrate goal/{goalPrefix}", branchName, goal.Id.Value, goalPrefix })
    {
        var result = GitCli.Run(
            executionDirectory,
            "log",
            "--format=%aI",
            "-n",
            "1",
            "--first-parent",
            "--regexp-ignore-case",
            $"--grep={pattern}",
            "HEAD");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            continue;
        }

        var line = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is not null && DateTimeOffset.TryParse(line, out authoredAt))
        {
            return true;
        }
    }

    return false;
}

private static string ResolveGoalStatusText(OrchestratorWorkspace workspace, Goal goal)
{
    var lifecycle = GoalLifecycle.ResolveState(goal, GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, goal));
    return DashboardResponseMapper.GoalStatusText(goal.Status, lifecycle);
}
}
