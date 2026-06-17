namespace Mcg.AgentOrchestrator.Core;

public sealed class Goal
{
    private readonly List<TaskSpec> _tasks;
    private readonly List<ProgressEvent> _timeline = [];
    private readonly HashSet<GoalId> _dependsOn = [];

    public Goal(GoalId id, string objective, IReadOnlyList<TaskSpec> tasks)
    {
        Id = id;
        Objective = RequireText(objective, nameof(objective));
        _tasks = tasks.Count == 0
            ? throw new ArgumentException("A goal must have at least one task.", nameof(tasks))
            : [.. tasks];
    }

    public GoalId Id { get; }

    public string Objective { get; }

    public GoalStatus Status { get; private set; } = GoalStatus.Draft;

    public string? SourceBacklogItemId { get; private set; }

    public IReadOnlyList<TaskSpec> Tasks => _tasks;

    public IReadOnlyList<ProgressEvent> Timeline => _timeline;

    public IReadOnlyCollection<GoalId> DependsOn => _dependsOn;

    internal void SetStatus(GoalStatus status) => Status = status;

    internal void SetSourceBacklogItemId(string id) => SourceBacklogItemId = id;

    internal void Append(ProgressEvent progressEvent) => _timeline.Add(progressEvent);

    internal void AddTask(TaskSpec task) => _tasks.Add(task);

    internal void AddDependency(GoalId dependencyId) => _dependsOn.Add(dependencyId);

    internal GoalSnapshot ToSnapshot()
    {
        return new GoalSnapshot(
            Id.Value,
            Objective,
            Status,
            _tasks.Select(task => task.ToSnapshot()).ToList(),
            _timeline.Select(evt => new ProgressEventSnapshot(
                evt.GoalId.Value,
                evt.TaskId?.Value,
                evt.Kind,
                evt.Message,
                evt.OccurredAt)).ToList(),
            _dependsOn.Count > 0 ? _dependsOn.Select(id => id.Value).ToList() : null,
            SourceBacklogItemId);
    }

    internal static Goal FromSnapshot(GoalSnapshot snapshot)
    {
        var goal = new Goal(new GoalId(snapshot.Id), snapshot.Objective, snapshot.Tasks.Select(TaskSpec.FromSnapshot).ToList());
        goal.SetStatus(snapshot.Status);

        foreach (var evt in snapshot.Timeline.OrderBy(item => item.OccurredAt))
        {
            goal.Append(new ProgressEvent(
                new GoalId(evt.GoalId),
                evt.TaskId is null ? null : new TaskId(evt.TaskId),
                evt.Kind,
                evt.Message,
                evt.OccurredAt));
        }

        foreach (var depId in snapshot.DependsOn ?? [])
        {
            goal.AddDependency(new GoalId(depId));
        }

        if (snapshot.SourceBacklogItemId is not null)
            goal.SetSourceBacklogItemId(snapshot.SourceBacklogItemId);

        return goal;
    }

    internal TaskSpec FindTask(TaskId taskId)
    {
        return _tasks.FirstOrDefault(task => task.Id == taskId)
            ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        return value.Trim();
    }
}
