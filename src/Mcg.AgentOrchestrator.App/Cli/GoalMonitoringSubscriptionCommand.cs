using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalMonitoringSubscriptionCommand
{
    private static readonly JsonSerializerOptions NdjsonOptions = new(DashboardJson.Options()) { WriteIndented = false };
    public const string GoalsSubscribeUsage = "Usage: goals subscribe [<goal-id>|--goal-prefix <prefix>] [--from-cursor <cursor>|--since <event-id>] [--task <id>] [--event-kind <kind,...>] [--once] [--wait-terminal] [--format ndjson|human] [--timeout <duration>]";
    private const string OnceFlag = "--once";
    private const string SinceFlag = "--since";
    private const string FromCursorFlag = "--from-cursor";
    private const string FormatFlag = "--format";
    private const string GoalPrefixFlag = "--goal-prefix";
    private const string TaskFlag = "--task";
    private const string EventKindFlag = "--event-kind";
    private const string WaitTerminalFlag = "--wait-terminal";
    private const string TimeoutFlag = "--timeout";

    public static GoalMonitoringSubscriptionOptions Parse(IReadOnlyList<string> parts)
    {
        var isGoalsSubscribe = parts.Count >= 2 &&
            parts[0].Equals("goals", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("subscribe", StringComparison.OrdinalIgnoreCase);
        parts = NormalizeCommandShape(parts);
        if (parts.Count < 2)
        {
            throw new ArgumentException(Usage);
        }

        var sinceEventId = 0L;
        string? fromCursor = null;
        var once = false;
        var waitTerminal = false;
        TimeSpan? timeout = null;
        var format = isGoalsSubscribe ? GoalMonitoringOutputFormat.Ndjson : GoalMonitoringOutputFormat.Sse;
        string? goalPrefix = null;
        string? taskId = null;
        var eventKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Uri? dashboardUri = null;
        string goalId;
        var optionStart = 2;
        if (IsDashboardUri(parts[1]))
        {
            if (parts.Count < 3)
            {
                throw new ArgumentException(Usage);
            }

            dashboardUri = CreateDashboardUri(parts[1]);
            goalId = parts[2];
            optionStart = 3;
        }
        else
        {
            goalId = parts[1];
        }

        for (var index = optionStart; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Equals(OnceFlag, StringComparison.OrdinalIgnoreCase))
            {
                once = true;
                continue;
            }

            if (part.Equals(WaitTerminalFlag, StringComparison.OrdinalIgnoreCase))
            {
                waitTerminal = true;
                continue;
            }

            if (part.Equals(SinceFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count || !long.TryParse(parts[index + 1], out sinceEventId) || sinceEventId < 0)
                {
                    throw new ArgumentException(Usage);
                }

                fromCursor = GoalStateSubscriptionCursor.Timeline(sinceEventId).ToString();
                index++;
                continue;
            }

            if (part.Equals(FromCursorFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count ||
                    !GoalStateSubscriptionCursor.TryParse(parts[index + 1], out var parsedCursor))
                {
                    throw new ArgumentException(Usage);
                }

                fromCursor = parts[index + 1];
                sinceEventId = Math.Max(parsedCursor.TimelineCursor, Math.Max(parsedCursor.RunEventCursor, parsedCursor.ProcessCursor));
                index++;
                continue;
            }

            if (part.Equals(FormatFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count || !TryParseOutputFormat(parts[index + 1], out format))
                {
                    throw new ArgumentException(Usage);
                }

                index++;
                continue;
            }

            if (part.Equals(GoalPrefixFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count || string.IsNullOrWhiteSpace(parts[index + 1]))
                {
                    throw new ArgumentException(Usage);
                }

                goalPrefix = parts[++index];
                continue;
            }

            if (part.Equals(TaskFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count || string.IsNullOrWhiteSpace(parts[index + 1]))
                {
                    throw new ArgumentException(Usage);
                }

                taskId = parts[++index];
                continue;
            }

            if (part.Equals(EventKindFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count)
                {
                    throw new ArgumentException(Usage);
                }

                foreach (var kind in parts[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    eventKinds.Add(kind);
                }

                continue;
            }

            if (part.Equals(TimeoutFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count || !TryParseTimeout(parts[index + 1], out var parsedTimeout))
                {
                    throw new ArgumentException(Usage);
                }

                timeout = parsedTimeout;
                index++;
                continue;
            }

            throw new ArgumentException($"Unknown monitor-goal option '{part}'.");
        }

        return new GoalMonitoringSubscriptionOptions(
            dashboardUri,
            goalId,
            sinceEventId,
            once,
            waitTerminal,
            format,
            goalPrefix,
            taskId,
            eventKinds.ToArray(),
            fromCursor,
            timeout);
    }

    internal static IReadOnlyList<string> NormalizeCommandShape(IReadOnlyList<string> parts)
    {
        if (parts.Count < 2 ||
            !parts[0].Equals("goals", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].Equals("subscribe", StringComparison.OrdinalIgnoreCase))
        {
            return parts;
        }

        var normalized = new List<string> { "monitor-goal" };
        normalized.AddRange(parts.Skip(2));
        if (normalized.Count >= 2 && !normalized[1].StartsWith("--", StringComparison.Ordinal))
        {
            return normalized;
        }

        var goalPrefix = FindFlagValue(normalized, GoalPrefixFlag);
        if (string.IsNullOrWhiteSpace(goalPrefix))
        {
            throw new ArgumentException(Usage);
        }

        normalized.Insert(1, goalPrefix);
        return normalized;
    }

    public static Uri BuildSnapshotUri(GoalMonitoringSubscriptionOptions options)
    {
        return BuildUri(options, stream: false);
    }

    public static Uri BuildStreamUri(GoalMonitoringSubscriptionOptions options)
    {
        return BuildUri(options, stream: true);
    }

    public static async Task RunAsync(IReadOnlyList<string> parts, TextWriter output, CancellationToken cancellationToken = default)
    {
        var options = Parse(parts);
        if (options.IsLocal)
        {
            throw new ArgumentException("Local monitor-goal requires orchestrator state. Use: monitor-goal <goal-id> [--since <event-id>] [--once]");
        }

        using var client = new HttpClient();

        if (options.Once)
        {
            var batch = await client.GetFromJsonAsync<GoalMonitoringBatchDto>(
                BuildSnapshotUri(options),
                DashboardJson.Options(),
                cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                throw new InvalidOperationException("Dashboard returned an empty monitoring response.");
            }

            PrintBatch(batch, output);
            return;
        }

        await using var stream = await client.GetStreamAsync(BuildStreamUri(options), cancellationToken).ConfigureAwait(false);
        await foreach (var serverEvent in ReadServerSentEventsAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            PrintServerSentEvent(serverEvent, output);
        }
    }

    public static async Task RunAsync(
        IReadOnlyList<string> parts,
        TextWriter output,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        Func<AgentOrchestratorKernel>? reloadKernel = null,
        CancellationToken cancellationToken = default)
    {
        var options = Parse(parts);
        if (!options.IsLocal)
        {
            await RunAsync(parts, output, cancellationToken).ConfigureAwait(false);
            return;
        }

        var runEvents = new SqliteRunEventStore(workspace.RunEventStorePath);
        if (options.Format == GoalMonitoringOutputFormat.Sse && !options.WaitTerminal)
        {
            await using var stream = new TextWriterStream(output);
            await GoalMonitoringStream.StreamAsync(
                stream,
                options.GoalId,
                _ => Task.FromResult(reloadKernel?.Invoke() ?? kernel),
                (current, goal, since) => GoalMonitoringStream.BuildBatch(current, goal, since, agents, workerProfiles, workspace),
                runEvents,
                options.SinceEventId,
                options.Once,
                TimeSpan.FromSeconds(1),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var terminal = await RunHeadlessLocalAsync(
            options,
            output,
            kernel,
            workspace,
            agents,
            workerProfiles,
            reloadKernel,
            runEvents,
            cancellationToken).ConfigureAwait(false);
        if (terminal is null)
        {
            throw new CliExitException(124);
        }

        if (options.WaitTerminal &&
            terminal is GoalLifecycleState.Failed or GoalLifecycleState.Blocked or GoalLifecycleState.AwaitingClarification or GoalLifecycleState.AwaitingHumanInput)
        {
            throw new CliExitException(1);
        }
    }

    public static void PrintBatch(GoalMonitoringBatchDto batch, TextWriter output, string? friendlyLabel = null)
    {
        PrintSnapshot(batch.Snapshot, output, friendlyLabel);
        foreach (var evt in batch.Events)
        {
            PrintTimelineEvent(evt, output);
        }
    }

    public static void PrintServerSentEvent(ServerSentEvent serverEvent, TextWriter output)
    {
        if (serverEvent.Event.Equals(DashboardMonitoringEvents.KeepAliveEventName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (serverEvent.Event.Equals("goal.snapshot", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = JsonSerializer.Deserialize<GoalMonitoringSnapshotDto>(serverEvent.Data, DashboardJson.Options());
            if (snapshot is not null)
            {
                PrintSnapshot(snapshot, output);
            }

            return;
        }

        if (serverEvent.Event.Equals("timeline", StringComparison.OrdinalIgnoreCase))
        {
            var evt = JsonSerializer.Deserialize<GoalMonitoringEventDto>(serverEvent.Data, DashboardJson.Options());
            if (evt is not null)
            {
                PrintTimelineEvent(evt, output);
            }

            return;
        }

        output.WriteLine($"event {serverEvent.Event} id={serverEvent.Id ?? "-"}");
    }

    public static async IAsyncEnumerable<ServerSentEvent> ReadServerSentEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? id = null;
        string? eventName = null;
        var data = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (eventName is not null || data.Length > 0)
                {
                    yield return new ServerSentEvent(id, eventName ?? "message", data.ToString());
                }

                id = null;
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                id = line["id:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.AppendLine();
                }

                data.Append(line["data:".Length..].TrimStart());
            }
        }
    }

    private static void PrintSnapshot(GoalMonitoringSnapshotDto snapshot, TextWriter output, string? friendlyLabel = null)
    {
        var running = snapshot.Tasks.Count(task => task.Status == WorkTaskStatus.Running);
        var failed = snapshot.Tasks.Count(task => task.Status == WorkTaskStatus.Failed);
        var completed = snapshot.Tasks.Count(task => task.Status == WorkTaskStatus.Completed);
        var capacity = snapshot.ProviderCapacity is null
            ? string.Empty
            : $" capacity={snapshot.ProviderCapacity.Disposition} ready={snapshot.ProviderCapacity.ReadyNowCount} deferred={snapshot.ProviderCapacity.DeferredCount}";
        var displayLabel = string.IsNullOrWhiteSpace(friendlyLabel)
            ? snapshot.GoalLabel
            : friendlyLabel;
        var label = string.IsNullOrWhiteSpace(displayLabel)
            ? string.Empty
            : $" ({displayLabel.Trim().ReplaceLineEndings(" ")})";
        output.WriteLine(
            $"snapshot goal={snapshot.GoalId[..Math.Min(8, snapshot.GoalId.Length)]}{label} status={snapshot.Monitor.StatusText} tasks={snapshot.Tasks.Count} completed={completed} running={running} failed={failed} lastEvent={snapshot.LastEventId} attention={snapshot.Monitor.Attention.Count} inbox={snapshot.OperatorInbox?.OpenCount ?? 0}{capacity}");
        if (snapshot.OperatorDisposition is not null)
        {
            output.WriteLine(
                $"disposition state={snapshot.OperatorDisposition.State} confidence={snapshot.OperatorDisposition.Confidence} action=\"{snapshot.OperatorDisposition.NextSafeCommand}\" blockers={snapshot.OperatorDisposition.Blockers.Count} reason=\"{snapshot.OperatorDisposition.Reason}\"");
        }
    }

    private static void PrintTimelineEvent(GoalMonitoringEventDto evt, TextWriter output)
    {
        var scope = evt.TaskNumber is null ? "goal" : $"task {evt.TaskNumber}";
        output.WriteLine($"event {evt.Id} {evt.OccurredAt:u} {evt.Kind} {scope}: {evt.Message}");
    }

    internal static bool Matches(GoalStateSubscriptionEvent evt, GoalMonitoringSubscriptionOptions options)
    {
        return (string.IsNullOrWhiteSpace(options.GoalPrefix) ||
                (evt.GoalId is not null && evt.GoalId.StartsWith(options.GoalPrefix, StringComparison.OrdinalIgnoreCase))) &&
            (string.IsNullOrWhiteSpace(options.TaskId) ||
                string.Equals(evt.TaskId, options.TaskId, StringComparison.OrdinalIgnoreCase)) &&
            ((options.EventKinds?.Count ?? 0) == 0 ||
                options.EventKinds!.Contains(evt.EventKind, StringComparer.OrdinalIgnoreCase));
    }

    internal static bool IsTerminalForWait(GoalLifecycleState state) =>
        state is GoalLifecycleState.Verified or GoalLifecycleState.Merged or GoalLifecycleState.Recorded or GoalLifecycleState.CleanedUp
            or GoalLifecycleState.Failed or GoalLifecycleState.Blocked or GoalLifecycleState.AwaitingClarification or GoalLifecycleState.AwaitingHumanInput;

    internal static string FormatHuman(GoalStateSubscriptionEvent evt)
    {
        var goalScope = evt.GoalId ?? "global";
        var scope = evt.TaskId is null ? goalScope : $"{goalScope}/{evt.TaskId}";
        var evidence = evt.ArtifactPath is not null ? $" artifact={evt.ArtifactPath}" : string.Empty;
        if (evt.ProcessId is not null)
        {
            evidence += $" process={evt.ProcessId.Value}";
        }

        return $"{evt.Timestamp:O} [{evt.EventKind}] {scope} -> {evt.CurrentState}  {evidence.Trim()}";
    }

    internal static IReadOnlyList<string> GoalStateEventSchemaFields() =>
    [
        "schemaVersion",
        "cursor",
        "timestamp",
        "eventKind",
        "goalId",
        "taskId",
        "currentState",
        "artifactPath",
        "processId",
        "message"
    ];

    private static async Task<GoalLifecycleState?> RunHeadlessLocalAsync(
        GoalMonitoringSubscriptionOptions options,
        TextWriter output,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        Func<AgentOrchestratorKernel>? reloadKernel,
        IRunEventStore runEvents,
        CancellationToken cancellationToken)
    {
        var resumeCursor = options.ResumeCursor;
        var timelineCursor = resumeCursor.TimelineCursor;
        var runEventCursor = resumeCursor.RunEventCursor;
        var processCursor = resumeCursor.ProcessCursor;
        var snapshotWritten = false;
        var deadline = options.Timeout is { } timeout
            ? DateTimeOffset.UtcNow.Add(timeout)
            : (DateTimeOffset?)null;
        while (true)
        {
            var current = reloadKernel?.Invoke() ?? kernel;
            var goal = OrchestratorEntityResolver.ResolveGoal(current, OrchestratorEntityResolver.GetLatestGoal(current), options.GoalId);
            var facts = ReadLifecycleFacts(workspace, goal);
            var state = GoalLifecycle.ResolveState(goal, facts);
            var batch = GoalMonitoringStream.BuildBatch(current, goal, timelineCursor, agents, workerProfiles, workspace);
            var runRecords = await runEvents.ReadSinceAsync(runEventCursor, maxCount: 500, cancellationToken: cancellationToken).ConfigureAwait(false);
            var envelopes = BuildSubscriptionEvents(batch, goal, state, workspace, runRecords);
            if (!snapshotWritten && !resumeCursor.IsEmpty && envelopes.Count == 0)
            {
                envelopes = [BuildSnapshotEvent(batch.Snapshot, state, workspace.RunEventStorePath, timelineCursor)];
            }

            var projectedEvents = ProjectCursorTokens(envelopes, timelineCursor, runEventCursor, processCursor);
            var eligibleEvents = projectedEvents
                .Where(evt => IsNewForCursor(evt, timelineCursor, runEventCursor, processCursor) || (resumeCursor.IsEmpty && !snapshotWritten))
                .Where(evt => Matches(evt, options))
                .ToList();
            foreach (var evt in eligibleEvents)
            {
                PrintSubscriptionEvent(evt, options.Format, output);
            }

            foreach (var evt in envelopes)
            {
                AdvanceCursor(evt, ref timelineCursor, ref runEventCursor, ref processCursor);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            snapshotWritten = true;

            var reachedTerminalState = options.WaitTerminal && IsTerminalForWait(state);
            if (options.Once || reachedTerminalState)
            {
                var fallbackWritten = PrintCurrentSnapshotWhenNoEligibleEvent(
                    eligibleEvents.Count,
                    batch,
                    state,
                    workspace,
                    timelineCursor,
                    runEventCursor,
                    processCursor,
                    options,
                    output);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (!options.Once || eligibleEvents.Count > 0 || fallbackWritten)
                {
                    return state;
                }
            }

            if (deadline is { } dueAt && DateTimeOffset.UtcNow >= dueAt)
            {
                var timeoutEvent = new GoalStateSubscriptionEvent(
                    1,
                    Math.Max(timelineCursor, 0),
                    DateTimeOffset.UtcNow,
                    "monitor.timeout",
                    goal.Id.Value,
                    null,
                    state.ToString(),
                    workspace.RunEventStorePath,
                    null,
                    $"timeout={options.Timeout!.Value}");
                PrintSubscriptionEvent(timeoutEvent with
                {
                    CursorToken = new GoalStateSubscriptionCursor(timelineCursor, runEventCursor, processCursor).ToString()
                }, options.Format, output);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var delay = TimeSpan.FromSeconds(1);
            if (deadline is { } nextDueAt)
            {
                delay = Min(delay, nextDueAt - DateTimeOffset.UtcNow);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool PrintCurrentSnapshotWhenNoEligibleEvent(
        int eligibleEventCount,
        GoalMonitoringBatchDto batch,
        GoalLifecycleState state,
        OrchestratorWorkspace workspace,
        long timelineCursor,
        long runEventCursor,
        long processCursor,
        GoalMonitoringSubscriptionOptions options,
        TextWriter output)
    {
        if (eligibleEventCount != 0)
        {
            return false;
        }

        var snapshot = BuildSnapshotEvent(batch.Snapshot, state, workspace.RunEventStorePath, timelineCursor) with
        {
            CursorToken = new GoalStateSubscriptionCursor(timelineCursor, runEventCursor, processCursor).ToString()
        };
        if (Matches(snapshot, options))
        {
            PrintSubscriptionEvent(snapshot, options.Format, output);
            return true;
        }

        return false;
    }

    internal static IReadOnlyList<GoalStateSubscriptionEvent> BuildSubscriptionEvents(
        GoalMonitoringBatchDto batch,
        Goal goal,
        GoalLifecycleState state,
        OrchestratorWorkspace workspace,
        IReadOnlyList<RunEventRecord> runRecords)
    {
        var events = new List<GoalStateSubscriptionEvent> { BuildSnapshotEvent(batch.Snapshot, state, workspace.RunEventStorePath, batch.LastEventId) };
        events.AddRange(batch.Events.Select(evt => BuildTimelineEnvelope(evt, goal, state, workspace)));
        events.AddRange(BuildProcessEnvelopes(batch, state));
        events.AddRange(runRecords
            .Where(record => record.GoalId is null || string.Equals(record.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase))
            .Select(record => new GoalStateSubscriptionEvent(
                1,
                record.Sequence,
                record.OccurredAt,
                record.Operation ?? record.EventType,
                record.GoalId,
                null,
                state.ToString(),
                workspace.RunEventStorePath,
                null,
                record.Detail)
                {
                    CursorDomain = GoalStateCursorDomain.RunEvent
                }));
        return events.OrderBy(evt => evt.CursorSequence).ThenBy(evt => evt.Timestamp).ToList();
    }

    private static IEnumerable<GoalStateSubscriptionEvent> BuildProcessEnvelopes(
        GoalMonitoringBatchDto batch,
        GoalLifecycleState state)
    {
        foreach (var task in batch.Snapshot.Tasks)
        {
            if (task.LastProcess is not { } process)
            {
                continue;
            }

            if (process.Heartbeat.IsAvailable)
            {
                var heartbeatAt = process.Heartbeat.LastObservedAt ?? batch.Snapshot.ObservedAt;
                yield return new GoalStateSubscriptionEvent(
                    1,
                    ProcessCursorSequence(task, process, heartbeatAt, "dispatch.heartbeat"),
                    heartbeatAt,
                    "dispatch.heartbeat",
                    batch.GoalId,
                    task.TaskId,
                    task.Status.ToString(),
                    process.HeartbeatPath ?? process.StandardOutputPath,
                    process.ProcessId,
                    $"state={process.Heartbeat.State} stdout={process.HeartbeatStdoutBytes ?? 0} stderr={process.HeartbeatStderrBytes ?? 0}")
                {
                    CursorDomain = GoalStateCursorDomain.Process
                };
            }

            if (!process.IsRunning && (process.CompletedAt is not null || process.ExitCode is not null))
            {
                var completedAt = process.CompletedAt ?? batch.Snapshot.ObservedAt;
                yield return new GoalStateSubscriptionEvent(
                    1,
                    ProcessCursorSequence(task, process, completedAt, "dispatch.exit"),
                    completedAt,
                    "dispatch.exit",
                    batch.GoalId,
                    task.TaskId,
                    state.ToString(),
                    process.ExitCodePath,
                    process.ProcessId,
                    $"exit={process.ExitCode?.ToString() ?? "unknown"} cancelled={process.WasCancelled}")
                {
                    CursorDomain = GoalStateCursorDomain.Process
                };
            }
        }
    }

    private static long ProcessCursorSequence(
        TaskMonitoringSnapshotDto task,
        ProcessDto process,
        DateTimeOffset timestamp,
        string eventKind)
    {
        var kindOffset = eventKind.Equals("dispatch.exit", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return timestamp.UtcTicks + (task.TaskNumber * 10) + Math.Abs(process.ProcessId % 10) + kindOffset;
    }

    private static GoalStateSubscriptionEvent BuildSnapshotEvent(GoalMonitoringSnapshotDto snapshot, GoalLifecycleState state, string artifactPath, long cursor) =>
        new(
            1,
            cursor,
            snapshot.ObservedAt,
            "goal.snapshot",
            snapshot.GoalId,
            null,
            state.ToString(),
            artifactPath,
            null,
            $"tasks={snapshot.Tasks.Count} attention={snapshot.Monitor.Attention.Count}")
        {
            CursorDomain = GoalStateCursorDomain.Timeline
        };

    private static GoalStateSubscriptionEvent BuildTimelineEnvelope(
        GoalMonitoringEventDto evt,
        Goal goal,
        GoalLifecycleState state,
        OrchestratorWorkspace workspace)
    {
        var task = evt.TaskId is null ? null : goal.Tasks.FirstOrDefault(candidate => candidate.Id.Value == evt.TaskId);
        var artifactPath = task?.LastVerification?.StandardOutputPath ?? task?.LastProcess?.StandardOutputPath ?? workspace.RunEventStorePath;
        return new GoalStateSubscriptionEvent(
            1,
            evt.Id,
            evt.OccurredAt,
            evt.Kind.ToString(),
            evt.GoalId,
            evt.TaskId,
            evt.TaskStatus?.ToString() ?? state.ToString(),
            artifactPath,
            task?.LastProcess?.ProcessId,
            evt.Message)
        {
            CursorDomain = GoalStateCursorDomain.Timeline
        };
    }

    internal static GoalLifecycleFacts ReadLifecycleFacts(OrchestratorWorkspace workspace, Goal goal)
    {
        var executionDirectory = workspace.ExecutionDirectory;
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var workspaceExists = worktree is not null;
        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var isMerged = GoalOperationJournal.HasCompletedLandingEvidence(journal);
        var isRecorded = GoalOperationJournal.HasCompletedRecordEvidence(journal);
        var isCleanedUp = GoalOperationJournal.HasCompletedCleanupEvidence(journal);
        var hasOpenClarification = GoalRefinementGate.HasOpenClarification(workspace, goal);
        var isBlocked = GoalAcceptanceStatusProjector.HasCurrentBlockingAcceptanceState(goal, executionDirectory, journal) ||
            journal.LatestByOperation.Any(entry =>
                entry.Status == GoalOperationStatus.Failed &&
                (entry.Operation.Contains("land", StringComparison.OrdinalIgnoreCase) ||
                 entry.Operation.Contains("cleanup", StringComparison.OrdinalIgnoreCase) ||
                 entry.Operation.Contains("workspace:remove", StringComparison.OrdinalIgnoreCase)));
        return new GoalLifecycleFacts(workspaceExists, isBlocked, isMerged, isRecorded, isCleanedUp, hasOpenClarification);
    }

    private static bool IsNewForCursor(GoalStateSubscriptionEvent evt, long timelineCursor, long runEventCursor, long processCursor)
    {
        var cursor = evt.CursorDomain switch
        {
            GoalStateCursorDomain.RunEvent => runEventCursor,
            GoalStateCursorDomain.Process => processCursor,
            _ => timelineCursor
        };
        return evt.CursorSequence > cursor;
    }

    private static IReadOnlyList<GoalStateSubscriptionEvent> ProjectCursorTokens(
        IReadOnlyList<GoalStateSubscriptionEvent> events,
        long timelineCursor,
        long runEventCursor,
        long processCursor)
    {
        var projected = new List<GoalStateSubscriptionEvent>(events.Count);
        foreach (var evt in events)
        {
            if (evt.CursorDomain == GoalStateCursorDomain.RunEvent)
            {
                runEventCursor = Math.Max(runEventCursor, evt.CursorSequence);
            }
            else if (evt.CursorDomain == GoalStateCursorDomain.Process)
            {
                processCursor = Math.Max(processCursor, evt.CursorSequence);
            }
            else
            {
                timelineCursor = Math.Max(timelineCursor, evt.CursorSequence);
            }

            projected.Add(evt with
            {
                CursorToken = new GoalStateSubscriptionCursor(timelineCursor, runEventCursor, processCursor).ToString()
            });
        }

        return projected;
    }

    private static void AdvanceCursor(GoalStateSubscriptionEvent evt, ref long timelineCursor, ref long runEventCursor, ref long processCursor)
    {
        if (evt.CursorDomain == GoalStateCursorDomain.RunEvent)
        {
            runEventCursor = Math.Max(runEventCursor, evt.CursorSequence);
            return;
        }

        if (evt.CursorDomain == GoalStateCursorDomain.Process)
        {
            processCursor = Math.Max(processCursor, evt.CursorSequence);
            return;
        }

        timelineCursor = Math.Max(timelineCursor, evt.CursorSequence);
    }

    private static void PrintSubscriptionEvent(GoalStateSubscriptionEvent evt, GoalMonitoringOutputFormat format, TextWriter output)
    {
        if (format == GoalMonitoringOutputFormat.Human)
        {
            output.WriteLine(FormatHuman(evt));
            return;
        }

        output.WriteLine(JsonSerializer.Serialize(evt, NdjsonOptions));
    }

    private static bool TryParseOutputFormat(string value, out GoalMonitoringOutputFormat format)
    {
        if (value.Equals("sse", StringComparison.OrdinalIgnoreCase))
        {
            format = GoalMonitoringOutputFormat.Sse;
            return true;
        }

        if (value.Equals("ndjson", StringComparison.OrdinalIgnoreCase))
        {
            format = GoalMonitoringOutputFormat.Ndjson;
            return true;
        }

        if (value.Equals("human", StringComparison.OrdinalIgnoreCase))
        {
            format = GoalMonitoringOutputFormat.Human;
            return true;
        }

        format = GoalMonitoringOutputFormat.Sse;
        return false;
    }

    private static bool TryParseTimeout(string value, out TimeSpan timeout)
    {
        timeout = TimeSpan.Zero;
        if (TimeSpan.TryParse(value, out var parsed) && parsed > TimeSpan.Zero)
        {
            timeout = parsed;
            return true;
        }

        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(value[..^2], out var milliseconds) &&
            milliseconds > 0)
        {
            timeout = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(value[..^1], out var seconds) &&
            seconds > 0)
        {
            timeout = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(value[..^1], out var minutes) &&
            minutes > 0)
        {
            timeout = TimeSpan.FromMinutes(minutes);
            return true;
        }

        return false;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static string? FindFlagValue(IReadOnlyList<string> parts, string flag)
    {
        for (var index = 0; index < parts.Count - 1; index++)
        {
            if (parts[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return parts[index + 1];
            }
        }

        return null;
    }

    private static Uri BuildUri(GoalMonitoringSubscriptionOptions options, bool stream)
    {
        if (options.DashboardUri is null)
        {
            throw new InvalidOperationException("Dashboard URI is not available for local monitor-goal mode.");
        }

        var goalId = Uri.EscapeDataString(options.GoalId);
        var path = stream ? $"api/goals/{goalId}/events/stream" : $"api/goals/{goalId}/events";
        var builder = new UriBuilder(new Uri(options.DashboardUri, path));
        if (options.SinceEventId > 0)
        {
            builder.Query = $"since={options.SinceEventId}";
        }

        return builder.Uri;
    }

    private static bool IsDashboardUri(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static Uri CreateDashboardUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Dashboard URL must be an absolute http or https URL.");
        }

        return value.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(value + "/");
    }

    private const string Usage = "Usage: goals subscribe [<goal-id>|--goal-prefix <prefix>] [--since <event-id>|--from-cursor <cursor>] [--once] [--format ndjson|human] [--task <id>] [--event-kind <kind,...>] [--wait-terminal] [--timeout <duration>], monitor-goal <goal-id> [--since <event-id>|--from-cursor <cursor>] [--once] [--format sse|ndjson|human] [--goal-prefix <prefix>] [--task <id>] [--event-kind <kind,...>] [--wait-terminal] [--timeout <duration>], or monitor-goal <dashboard-url> <goal-id> [--since <event-id>] [--once]. --wait-terminal wakes on completed, failed, abandoned/cancelled, blocked, or awaiting-human-input states; timeout accepts TimeSpan, ms, s, or m.";

    private sealed class TextWriterStream(TextWriter writer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => writer.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => writer.Write(Encoding.UTF8.GetString(buffer, offset, count));
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await writer.WriteAsync(Encoding.UTF8.GetString(buffer.Span)).ConfigureAwait(false);
        }
    }
}

internal enum GoalMonitoringOutputFormat
{
    Sse,
    Ndjson,
    Human
}

internal sealed record GoalStateSubscriptionEvent(
    [property: JsonPropertyName("schemaVersion")]
    int SchemaVersion,
    [property: JsonIgnore]
    long CursorSequence,
    [property: JsonPropertyName("timestamp")]
    DateTimeOffset Timestamp,
    [property: JsonPropertyName("eventKind")]
    string EventKind,
    [property: JsonPropertyName("goalId")]
    string? GoalId,
    [property: JsonPropertyName("taskId")]
    string? TaskId,
    [property: JsonPropertyName("currentState")]
    string CurrentState,
    [property: JsonPropertyName("artifactPath")]
    string? ArtifactPath,
    [property: JsonPropertyName("processId")]
    int? ProcessId,
    [property: JsonPropertyName("message")]
    string? Message)
{
    [JsonPropertyName("cursor")]
    public string Cursor => CursorToken ?? GoalStateSubscriptionCursor.ForDomain(CursorDomain, CursorSequence).ToString();

    [JsonIgnore]
    public string? CursorToken { get; init; }

    [JsonIgnore]
    public GoalStateCursorDomain CursorDomain { get; init; }
}

internal readonly record struct GoalStateSubscriptionCursor(long TimelineCursor, long RunEventCursor, long ProcessCursor = 0)
{
    public bool IsEmpty => TimelineCursor <= 0 && RunEventCursor <= 0 && ProcessCursor <= 0;

    public static GoalStateSubscriptionCursor Empty => new(0, 0);

    public static GoalStateSubscriptionCursor Timeline(long cursor) => new(Math.Max(0, cursor), 0);

    public static GoalStateSubscriptionCursor RunEvent(long cursor) => new(0, Math.Max(0, cursor));

    public static GoalStateSubscriptionCursor Process(long cursor) => new(0, 0, Math.Max(0, cursor));

    public static GoalStateSubscriptionCursor ForDomain(GoalStateCursorDomain domain, long cursor) =>
        domain switch
        {
            GoalStateCursorDomain.RunEvent => RunEvent(cursor),
            GoalStateCursorDomain.Process => Process(cursor),
            _ => Timeline(cursor)
        };

    public static bool TryParse(string? value, out GoalStateSubscriptionCursor cursor)
    {
        cursor = Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (long.TryParse(value, out var legacyTimelineCursor) && legacyTimelineCursor >= 0)
        {
            cursor = Timeline(legacyTimelineCursor);
            return true;
        }

        long timeline = 0;
        long runEvent = 0;
        long process = 0;
        var sawPart = false;
        foreach (var rawPart in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = rawPart.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == rawPart.Length - 1)
            {
                return false;
            }

            var domain = rawPart[..separator];
            if (!long.TryParse(rawPart[(separator + 1)..], out var sequence) || sequence < 0)
            {
                return false;
            }

            if (domain.Equals("timeline", StringComparison.OrdinalIgnoreCase) ||
                domain.Equals("t", StringComparison.OrdinalIgnoreCase))
            {
                timeline = sequence;
                sawPart = true;
                continue;
            }

            if (domain.Equals("run-event", StringComparison.OrdinalIgnoreCase) ||
                domain.Equals("runEvent", StringComparison.OrdinalIgnoreCase) ||
                domain.Equals("r", StringComparison.OrdinalIgnoreCase))
            {
                runEvent = sequence;
                sawPart = true;
                continue;
            }

            if (domain.Equals("process", StringComparison.OrdinalIgnoreCase) ||
                domain.Equals("p", StringComparison.OrdinalIgnoreCase))
            {
                process = sequence;
                sawPart = true;
                continue;
            }

            return false;
        }

        cursor = new GoalStateSubscriptionCursor(timeline, runEvent, process);
        return sawPart;
    }

    public static GoalStateSubscriptionCursor Parse(string? token, long fallbackTimelineCursor)
    {
        if (TryParse(token, out var cursor))
        {
            return cursor;
        }

        return Timeline(fallbackTimelineCursor);
    }

    public override string ToString()
    {
        var parts = new List<string>(3);
        if (TimelineCursor > 0)
        {
            parts.Add($"timeline:{TimelineCursor}");
        }

        if (RunEventCursor > 0)
        {
            parts.Add($"run-event:{RunEventCursor}");
        }

        if (ProcessCursor > 0)
        {
            parts.Add($"process:{ProcessCursor}");
        }

        return parts.Count == 0 ? "timeline:0" : string.Join(';', parts);
    }
}

internal enum GoalStateCursorDomain
{
    Timeline,
    RunEvent,
    Process
}

internal sealed record GoalMonitoringSubscriptionOptions(
    Uri? DashboardUri,
    string GoalId,
    long SinceEventId,
    bool Once,
    bool WaitTerminal = false,
    GoalMonitoringOutputFormat Format = GoalMonitoringOutputFormat.Sse,
    string? GoalPrefix = null,
    string? TaskId = null,
    IReadOnlyList<string>? EventKinds = null,
    string? FromCursor = null,
    TimeSpan? Timeout = null)
{
    public bool IsLocal => DashboardUri is null;

    public GoalStateSubscriptionCursor ResumeCursor => GoalStateSubscriptionCursor.Parse(FromCursor, SinceEventId);
}

internal sealed record ServerSentEvent(string? Id, string Event, string Data);
