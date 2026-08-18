using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalBoardCommand
{
    internal static bool IsBoardCommand(IReadOnlyList<string> args) =>
        args.Count >= 2 &&
        args[0].Equals("goals", StringComparison.OrdinalIgnoreCase) &&
        args[1].Equals("--board", StringComparison.OrdinalIgnoreCase) &&
        !args.Any(arg => arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase));

    internal static void Run(
        IReadOnlyList<string> args,
        IOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        Func<DateTimeOffset>? utcNow = null,
        Func<ProcessCommandLineSnapshot>? processSnapshotFactory = null,
        Func<string, IEnumerable<GoalId>, IReadOnlyDictionary<GoalId, string>>? worktreeResolver = null,
        Func<string, GitCli.WorktreeStatusInspection>? worktreeStatusInspector = null,
        Func<string, GitCli.AheadBehindInspection>? aheadBehindInspector = null)
    {
        var options = GoalBoardOptions.Parse(args);
        var now = (utcNow ?? (() => DateTimeOffset.UtcNow))();
        var metadata = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var includedIds = metadata
            .Where(summary => Enum.TryParse<GoalStatus>(summary.Status, ignoreCase: true, out var status) && GoalBoardProjector.IsIncluded(status))
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        var kernel = stateRepository.LoadGoalsAsync(includedIds).GetAwaiter().GetResult();
        var goals = kernel.Goals.Where(goal => GoalBoardProjector.IsIncluded(goal.Status)).ToArray();
        var goalIds = goals.Select(goal => goal.Id.Value).ToArray();

        var attention = ReadAttention(workspace, goalIds);
        var intents = ReadIntents(workspace, goalIds);
        var acceptance = GoalBoardAcceptanceAttemptReader.Read(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
            goalIds);
        var worktrees = InspectWorktrees(
            workspace.ExecutionDirectory,
            goals.Select(goal => goal.Id),
            worktreeResolver,
            worktreeStatusInspector,
            aheadBehindInspector);

        // Machine process discovery is deliberately captured once for the entire board. Every goal
        // disposition evaluates against this immutable snapshot.
        var processSnapshot = (processSnapshotFactory ?? ProcessCommandLines.Snapshot)();
        var clock = new BoardClock(now);
        var dispositionSurface = new GoalOperatorDispositionSurface(
            clock,
            new DispatchStateSurface(clock, inspectWorktree: false));

        var facts = goals.Select(goal => BuildFact(
            goal,
            kernel,
            attention,
            intents,
            acceptance,
            worktrees,
            dispositionSurface,
            processSnapshot)).ToArray();
        var projection = GoalBoardProjector.Project(facts, options, now);
        foreach (var row in projection.Rows)
            Console.WriteLine(row);
        Console.WriteLine($"shown={projection.Shown} omitted={projection.Omitted}");
        if (projection.RerunInstruction is not null)
            Console.WriteLine(projection.RerunInstruction);
    }

    private static GoalBoardGoalFact BuildFact(
        Goal goal,
        AgentOrchestratorKernel kernel,
        AttentionRead attentionRead,
        IntentRead intentRead,
        IReadOnlyDictionary<string, GoalBoardAcceptanceFact> acceptance,
        IReadOnlyDictionary<GoalId, GoalBoardWorktreeFact> worktrees,
        GoalOperatorDispositionSurface dispositionSurface,
        ProcessCommandLineSnapshot processSnapshot)
    {
        attentionRead.Summaries.TryGetValue(goal.Id.Value, out var attention);
        intentRead.Summaries.TryGetValue(goal.Id.Value, out var intent);
        acceptance.TryGetValue(goal.Id.Value, out var acceptanceFact);
        acceptanceFact ??= GoalBoardAcceptanceFact.Unknown;

        GoalOperatorDisposition? disposition = null;
        try
        {
            disposition = dispositionSurface.Evaluate(
                goal,
                pendingHumanInputCount: 0,
                verificationSatisfied: false,
                executionDirectory: null,
                commandLineSnapshot: processSnapshot,
                skipTerminalDispatchEvaluation: false,
                skipInactiveDispatchEvaluation: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Process/artifact facts are optional for the board. Preserve unknown instead of
            // inventing a live/dead state from a failed observation.
        }

        var dispatch = disposition?.Dispatches
            .Where(candidate => candidate.DispatchState is not null)
            .OrderByDescending(candidate => candidate.FreshAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        var liveWorker = dispatch?.DispatchState?.Kind is DispatchStateKind.ActiveTestChild or DispatchStateKind.Running;
        var deadDispatch = dispatch?.DispatchState?.Kind is
            DispatchStateKind.ExitedAwaitingReconcile or
            DispatchStateKind.InterruptedWork or
            DispatchStateKind.StaleCleanup or
            DispatchStateKind.WedgedProcess;
        var work = acceptanceFact.IsLive
            ? "gate:live"
            : dispatch is not null
                ? $"{Role(dispatch.Role)}:{DispatchState(dispatch.DispatchState?.Kind)}"
                : CurrentTaskWork(goal);
        var signals = new List<GoalBoardSignalFact>
        {
            new("goal", goal.Timeline.OrderByDescending(evt => evt.OccurredAt).FirstOrDefault()?.OccurredAt),
            new("dispatch", dispatch?.FreshAt),
            new("acceptance", acceptanceFact.IsLive ? acceptanceFact.LastHeartbeatAt : null),
            new("attention", attention?.LatestAt),
            new("intent", intent?.LatestAt)
        };
        foreach (var task in goal.Tasks)
        {
            if (task.LastProcess is { } process)
            {
                signals.Add(new GoalBoardSignalFact("dispatch", process.CompletedAt ?? process.StartedAt));
                if (DispatchExitArtifacts.TryRead(process.ExitCodePath, out var exitArtifact))
                    signals.Add(new GoalBoardSignalFact("dispatch", exitArtifact.RecordedAt));
            }
            else if (task.LastDispatch is { } recordedDispatch)
            {
                signals.Add(new GoalBoardSignalFact("dispatch", recordedDispatch.DispatchedAt));
            }
        }

        var stageInput = new StatusProjectionGoal(
            goal.Id.Value,
            goal.Objective,
            goal.Status,
            goal.Tasks.Select(task => new StatusProjectionTask(task.RequiredRole, task.Status)).ToArray(),
            goal.Timeline.OrderByDescending(evt => evt.OccurredAt).FirstOrDefault()?.OccurredAt);
        return new GoalBoardGoalFact(
            goal.Id.Value,
            goal.Objective,
            goal.Status,
            StatusProjector.DetermineStage(stageInput),
            work,
            signals,
            attentionRead.Available ? attention?.Count ?? 0 : null,
            intentRead.Available ? intent?.Count ?? 0 : null,
            Backlog(goal),
            worktrees.TryGetValue(goal.Id, out var worktree) ? worktree : GoalBoardWorktreeFact.Unknown,
            deadDispatch,
            deadDispatch ? dispatch?.NextSafeCommand : null,
            acceptanceFact.IsLive,
            liveWorker,
            Fallback(kernel, goal));
    }

    private static AttentionRead ReadAttention(OrchestratorWorkspace workspace, IReadOnlyCollection<string> goalIds)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        if (!File.Exists(path))
            return AttentionRead.Unavailable;
        try
        {
            var items = CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory)
                .ListForGoalIdsAsync(goalIds).GetAwaiter().GetResult();
            var summaries = CollaborationItemLifecycle.BuildAttentionQueue(items)
                .Where(item => item.GoalId is not null)
                .GroupBy(item => item.GoalId!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => new AttentionSummary(group.Count(), group.Max(item => item.RaisedAt)),
                    StringComparer.Ordinal);
            return new AttentionRead(true, summaries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return AttentionRead.Unavailable;
        }
    }

    private static IntentRead ReadIntents(OrchestratorWorkspace workspace, IReadOnlyCollection<string> goalIds)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        if (!File.Exists(path))
            return IntentRead.Unavailable;
        try
        {
            var summaries = SqliteOperatorIntentStore.OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory)
                .ListActionableSummariesAsync(goalIds).GetAwaiter().GetResult();
            return new IntentRead(true, summaries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return IntentRead.Unavailable;
        }
    }

    private static IReadOnlyDictionary<GoalId, GoalBoardWorktreeFact> InspectWorktrees(
        string executionDirectory,
        IEnumerable<GoalId> goalIds,
        Func<string, IEnumerable<GoalId>, IReadOnlyDictionary<GoalId, string>>? worktreeResolver,
        Func<string, GitCli.WorktreeStatusInspection>? worktreeStatusInspector,
        Func<string, GitCli.AheadBehindInspection>? aheadBehindInspector)
    {
        var ids = goalIds.ToArray();
        IReadOnlyDictionary<GoalId, string> resolved;
        try
        {
            resolved = (worktreeResolver ?? GoalWorktrees.ResolveAll)(executionDirectory, ids);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ids.ToDictionary(id => id, _ => GoalBoardWorktreeFact.Unknown);
        }

        var facts = new Dictionary<GoalId, GoalBoardWorktreeFact>();
        foreach (var id in ids)
        {
            if (!resolved.TryGetValue(id, out var path))
            {
                facts[id] = GoalBoardWorktreeFact.Absent;
                continue;
            }

            var status = (worktreeStatusInspector ?? GitCli.InspectWorktreeStatus)(path);
            var divergence = (aheadBehindInspector ?? GitCli.InspectAheadBehind)(path);
            facts[id] = status.Succeeded && divergence.Succeeded
                ? new GoalBoardWorktreeFact(status.IsDirty ? "dirty" : "clean", divergence.Ahead, divergence.Behind)
                : GoalBoardWorktreeFact.Unknown;
        }

        return facts;
    }

    private static string CurrentTaskWork(Goal goal)
    {
        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Status != WorkTaskStatus.Completed) ?? goal.Tasks.LastOrDefault();
        return task is null ? "none" : $"{Role(task.RequiredRole)}:{Kebab(task.Status.ToString())}";
    }

    private static string Fallback(AgentOrchestratorKernel kernel, Goal goal)
    {
        try
        {
            var action = kernel.BuildNextActions(goal.Id).Items.FirstOrDefault();
            return action is null
                ? $"next {Prefix(goal.Id.Value)} --full"
                : ConsoleViews.BuildSuggestedCommand(goal, action);
        }
        catch (InvalidOperationException)
        {
            return $"next {Prefix(goal.Id.Value)} --full";
        }
    }

    private static string Backlog(Goal goal)
    {
        if (string.IsNullOrWhiteSpace(goal.SourceBacklogItemId))
            return "-";
        var coverage = goal.SourceBacklogCoverage?.ToString().ToLowerInvariant() ?? "unknown";
        return $"{Prefix(goal.SourceBacklogItemId)}/{coverage}";
    }

    private static string DispatchState(DispatchStateKind? state) =>
        state is null ? "unknown" : Kebab(state.Value.ToString());

    private static string Role(AgentRole role) => role.ToString().ToLowerInvariant();

    private static string Kebab(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"-{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));

    private static string Prefix(string value) => value[..Math.Min(8, value.Length)].ToLowerInvariant();

    private sealed class BoardClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed record AttentionSummary(int Count, DateTimeOffset LatestAt);
    private sealed record AttentionRead(bool Available, IReadOnlyDictionary<string, AttentionSummary> Summaries)
    {
        public static AttentionRead Unavailable { get; } = new(
            false,
            new Dictionary<string, AttentionSummary>(StringComparer.Ordinal));
    }

    private sealed record IntentRead(
        bool Available,
        IReadOnlyDictionary<string, ActionableOperatorIntentSummary> Summaries)
    {
        public static IntentRead Unavailable { get; } = new(
            false,
            new Dictionary<string, ActionableOperatorIntentSummary>(StringComparer.Ordinal));
    }
}
