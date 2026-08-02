namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private readonly Dictionary<GoalId, Goal> _goals = [];
    private readonly Dictionary<HumanInputRequestId, HumanInputRequest> _humanInputRequests = [];
    private readonly HashSet<GoalId> _knownCompletedDependencyGoals = [];
    private readonly Dictionary<GoalId, string> _knownDependencyGoalStatuses = [];
    private readonly IClock _clock;
    private IGoalLifecycleEventWriter _eventWriter = NullGoalLifecycleEventWriter.Instance;

    public AgentOrchestratorKernel(IClock? clock = null)
    {
        _clock = clock ?? new SystemClock();
    }

    public void SetEventWriter(IGoalLifecycleEventWriter writer)
    {
        _eventWriter = writer ?? NullGoalLifecycleEventWriter.Instance;
    }

    public IReadOnlyCollection<Goal> Goals => _goals.Values;

    public IReadOnlyCollection<HumanInputRequest> HumanInputRequests => _humanInputRequests.Values;

    public IReadOnlyCollection<GoalId> KnownCompletedDependencyGoals => _knownCompletedDependencyGoals;

    public IReadOnlyDictionary<GoalId, string> KnownDependencyGoalStatuses => _knownDependencyGoalStatuses;

    public void MarkKnownCompletedDependencyGoals(IEnumerable<GoalId> goalIds)
    {
        foreach (var goalId in goalIds)
        {
            _knownCompletedDependencyGoals.Add(goalId);
            _knownDependencyGoalStatuses.TryAdd(goalId, GoalStatus.Completed.ToString());
        }
    }

    public bool IsKnownCompletedDependencyGoal(GoalId goalId) =>
        _knownCompletedDependencyGoals.Contains(goalId);

    public void MarkKnownDependencyGoalStatuses(IEnumerable<KeyValuePair<GoalId, string>> goalStatuses)
    {
        foreach (var (goalId, status) in goalStatuses)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                continue;
            }

            _knownDependencyGoalStatuses[goalId] = status;
        }
    }

    public bool TryGetKnownDependencyGoalStatus(GoalId goalId, out string status) =>
        _knownDependencyGoalStatuses.TryGetValue(goalId, out status!);

    public OrchestratorSnapshot ExportSnapshot()
    {
        return new OrchestratorSnapshot(
            _goals.Values.Where(goal => !goal.IsMetadataOnly).Select(goal => goal.ToSnapshot()).ToList(),
            _humanInputRequests.Values.Select(request => request.ToSnapshot()).ToList());
    }

    public static AgentOrchestratorKernel FromSnapshot(OrchestratorSnapshot snapshot, IClock? clock = null)
    {
        var kernel = new AgentOrchestratorKernel(clock);

        foreach (var goal in snapshot.Goals.Select(Goal.FromSnapshot))
        {
            kernel._goals.Add(goal.Id, goal);
            kernel._knownDependencyGoalStatuses[goal.Id] = goal.Status.ToString();
        }

        foreach (var request in snapshot.HumanInputRequests.Select(HumanInputRequest.FromSnapshot))
        {
            kernel._humanInputRequests.Add(request.Id, request);
        }

        kernel.RepairMissingHumanInputRequests();
        kernel.SweepParkedGoalHumanWaits();
        return kernel;
    }

    public void ReplaceWithSnapshot(OrchestratorSnapshot snapshot)
    {
        _goals.Clear();
        _humanInputRequests.Clear();
        _knownCompletedDependencyGoals.Clear();
        _knownDependencyGoalStatuses.Clear();

        foreach (var goal in snapshot.Goals.Select(Goal.FromSnapshot))
        {
            _goals.Add(goal.Id, goal);
            _knownDependencyGoalStatuses[goal.Id] = goal.Status.ToString();
        }

        foreach (var request in snapshot.HumanInputRequests.Select(HumanInputRequest.FromSnapshot))
        {
            _humanInputRequests.Add(request.Id, request);
        }

        RepairMissingHumanInputRequests();
        SweepParkedGoalHumanWaits();
    }

    // Additive merge: ingest goals (and their human-input requests) from the snapshot that this kernel
    // does NOT already track, leaving every already-tracked goal's live in-flight state untouched. This
    // is the dynamic-goal-pickup primitive — a long-lived kernel (the daemon's continuous conductor
    // loop, or an interim per-tick reload) calls it to discover goals submitted AFTER it loaded, without
    // the clobber that FromSnapshot/ReplaceWithSnapshot would inflict on goals it is actively driving.
    // Returns the number of newly ingested goals.
    public int IngestNewGoals(OrchestratorSnapshot snapshot)
    {
        var ingested = 0;
        foreach (var goal in snapshot.Goals.Select(Goal.FromSnapshot))
        {
            if (IsTerminalGoalStatus(goal.Status))
            {
                TrackTerminalGoalMetadata(goal.ToTerminalGoalMetadata());
                continue;
            }

            if (!_goals.TryAdd(goal.Id, goal))
            {
                continue;
            }

            ingested++;
            _knownDependencyGoalStatuses[goal.Id] = goal.Status.ToString();
        }

        foreach (var request in snapshot.HumanInputRequests.Select(HumanInputRequest.FromSnapshot))
        {
            if (!_goals.ContainsKey(request.GoalId))
            {
                continue;
            }

            _humanInputRequests.TryAdd(request.Id, request);
        }

        RepairMissingHumanInputRequests();
        SweepParkedGoalHumanWaits();
        return ingested;
    }

    public int AddTerminalGoalMetadataOnlyStubs(IEnumerable<TerminalGoalMetadata> goals)
    {
        var changed = 0;
        foreach (var metadata in goals)
        {
            if (TrackTerminalGoalMetadata(metadata))
                changed++;
        }

        return changed;
    }

    public int EvictTerminalGoalAggregates(IEnumerable<GoalId>? goalIds = null)
    {
        var selected = goalIds is null
            ? null
            : goalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var evicted = 0;
        foreach (var goal in _goals.Values.ToArray())
        {
            if (!IsTerminalGoalStatus(goal.Status) ||
                selected is not null && !selected.Contains(goal.Id.Value))
            {
                continue;
            }

            var metadata = goal.ToTerminalGoalMetadata();
            if (_goals.Remove(goal.Id))
                evicted++;
            TrackTerminalGoalMetadata(metadata);
        }

        return evicted;
    }

    // Refreshes already-tracked goals from persisted state at a conductor tick boundary. Unlike
    // IngestNewGoals, this deliberately replaces known goals so externally persisted worker
    // completion is visible to the next role-handoff decision.
    public int RefreshTrackedGoals(OrchestratorSnapshot snapshot)
    {
        var refreshed = 0;
        foreach (var goal in snapshot.Goals.Select(Goal.FromSnapshot))
        {
            if (!_goals.ContainsKey(goal.Id))
            {
                continue;
            }

            if (IsTerminalGoalStatus(goal.Status))
            {
                _goals.Remove(goal.Id);
                TrackTerminalGoalMetadata(goal.ToTerminalGoalMetadata());
                refreshed++;
                continue;
            }

            _goals[goal.Id] = goal;
            _knownDependencyGoalStatuses[goal.Id] = goal.Status.ToString();
            refreshed++;
        }

        foreach (var request in snapshot.HumanInputRequests.Select(HumanInputRequest.FromSnapshot))
        {
            if (_humanInputRequests.ContainsKey(request.Id))
            {
                _humanInputRequests[request.Id] = request;
            }
        }

        RepairMissingHumanInputRequests();
        SweepParkedGoalHumanWaits();
        return refreshed;
    }

    private void RepairMissingHumanInputRequests()
    {
        foreach (var goal in _goals.Values.Where(goal =>
                     goal.Status != GoalStatus.Parked &&
                     !IsTerminalGoalStatus(goal.Status)))
        {
            var clearedWait = false;
            foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.WaitingForHuman))
            {
                if (_humanInputRequests.Values.Any(request =>
                        request.GoalId == goal.Id &&
                        request.TaskId == task.Id &&
                        !request.IsCompleted))
                {
                    continue;
                }

                var requested = goal.Timeline
                    .Where(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested)
                    .OrderByDescending(evt => evt.OccurredAt)
                    .FirstOrDefault();
                if (requested is null)
                {
                    RestoreTaskAfterHumanInput(goal, task);
                    clearedWait = true;
                    continue;
                }

                var requestId = BuildRepairedHumanInputRequestId(goal.Id, task.Id, requested);
                _humanInputRequests.TryAdd(
                    requestId,
                    new HumanInputRequest(
                        requestId,
                        goal.Id,
                        task.Id,
                        requested.Message,
                        requested.OccurredAt));
            }

            if (clearedWait)
            {
                RefreshGoalStatus(goal);
            }
        }
    }

    private static HumanInputRequestId BuildRepairedHumanInputRequestId(
        GoalId goalId,
        TaskId taskId,
        ProgressEvent requested)
    {
        var seed = $"{goalId.Value}\n{taskId.Value}\n{requested.OccurredAt:O}\n{requested.Message}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return new HumanInputRequestId(Convert.ToHexString(hash).ToLowerInvariant()[..32]);
    }

    private bool TrackTerminalGoalMetadata(TerminalGoalMetadata metadata)
    {
        var stub = Goal.CreateMetadataOnlyTerminal(metadata);
        var changed = false;
        if (_goals.Remove(stub.Id))
            changed = true;

        foreach (var request in _humanInputRequests.Values.Where(request => request.GoalId == stub.Id).ToArray())
        {
            if (_humanInputRequests.Remove(request.Id))
                changed = true;
        }

        if (!_knownDependencyGoalStatuses.TryGetValue(stub.Id, out var knownStatus) ||
            !string.Equals(knownStatus, stub.Status.ToString(), StringComparison.Ordinal))
        {
            _knownDependencyGoalStatuses[stub.Id] = stub.Status.ToString();
            changed = true;
        }

        if (stub.Status != GoalStatus.Completed && _knownCompletedDependencyGoals.Remove(stub.Id))
        {
            changed = true;
        }

        return changed;
    }




}
