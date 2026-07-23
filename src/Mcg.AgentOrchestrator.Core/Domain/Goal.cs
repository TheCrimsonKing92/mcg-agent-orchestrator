namespace Mcg.AgentOrchestrator.Core;

public sealed class Goal
{
    private readonly List<TaskSpec> _tasks;
    private readonly List<ProgressEvent> _timeline = [];
    private readonly List<EffectiveAcceptanceCriteriaCorrection> _effectiveAcceptanceCriteriaCorrections = [];
    private readonly HashSet<GoalId> _dependsOn = [];

    public Goal(GoalId id, string objective, IReadOnlyList<TaskSpec> tasks)
        : this(id, objective, tasks, isMetadataOnly: false)
    {
    }

    private Goal(GoalId id, string objective, IReadOnlyList<TaskSpec> tasks, bool isMetadataOnly)
    {
        Id = id;
        Objective = RequireText(objective, nameof(objective));
        if (!isMetadataOnly && tasks.Count == 0)
        {
            throw new ArgumentException("A goal must have at least one task.", nameof(tasks));
        }

        _tasks = tasks.Count == 0
            ? []
            : [.. tasks];
        IsMetadataOnly = isMetadataOnly;
    }

    public GoalId Id { get; }

    public string Objective { get; }

    public GoalStatus Status { get; private set; } = GoalStatus.Draft;

    public RefinedSpec? RefinedSpec { get; private set; }

    public AcceptanceFailureSummary? LatestAcceptanceFailure { get; private set; }

    public string? SourceBacklogItemId { get; private set; }

    public IReadOnlyList<TaskSpec> Tasks => _tasks;

    public IReadOnlyList<ProgressEvent> Timeline => _timeline;

    public IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> EffectiveAcceptanceCriteriaCorrections => _effectiveAcceptanceCriteriaCorrections;

    public IReadOnlyCollection<GoalId> DependsOn => _dependsOn;

    public bool IsMetadataOnly { get; }

    public string? MetadataResultCommit { get; private set; }

    public DateTimeOffset? MetadataCreatedAt { get; private set; }

    public DateTimeOffset? MetadataTerminatedAt { get; private set; }

    internal static Goal CreateMetadataOnlyTerminal(TerminalGoalMetadata metadata)
    {
        if (!IsTerminalMetadataStatus(metadata.Status))
        {
            throw new ArgumentException($"Status '{metadata.Status}' is not terminal.", nameof(metadata));
        }

        var goal = new Goal(metadata.Id, metadata.Title, [], isMetadataOnly: true);
        goal.SetStatus(metadata.Status);
        goal.MetadataResultCommit = NormalizeSha(metadata.ResultCommit);
        goal.MetadataCreatedAt = metadata.CreatedAt;
        goal.MetadataTerminatedAt = metadata.TerminatedAt;
        return goal;
    }

    internal TerminalGoalMetadata ToTerminalGoalMetadata()
    {
        if (!IsTerminalMetadataStatus(Status))
        {
            throw new InvalidOperationException($"Goal '{Id.Value}' is not terminal and cannot be represented as terminal metadata.");
        }

        var latestResultCommit = Tasks
            .Select(task => task.LastDispatch?.ResultCommit)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .LastOrDefault();
        var orderedTimeline = Timeline.OrderBy(evt => evt.OccurredAt).ToArray();
        return new TerminalGoalMetadata(
            Id,
            Status,
            BuildMetadataTitle(Objective),
            latestResultCommit,
            orderedTimeline.FirstOrDefault()?.OccurredAt,
            orderedTimeline.LastOrDefault()?.OccurredAt);
    }

    internal void SetStatus(GoalStatus status) => Status = status;

    internal void SetRefinedSpec(RefinedSpec spec) => RefinedSpec = spec;

    internal void SetSourceBacklogItemId(string id) => SourceBacklogItemId = id;

    internal void RecordAcceptanceFailure(
        IReadOnlyList<string> failedChecks,
        DateTimeOffset occurredAt,
        string? branchHeadSha = null,
        string? mainHeadSha = null)
    {
        var checks = failedChecks
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        LatestAcceptanceFailure = checks.Length == 0
            ? null
            : new AcceptanceFailureSummary(occurredAt, checks, NormalizeSha(branchHeadSha), NormalizeSha(mainHeadSha));
    }

    internal void ClearAcceptanceFailure() => LatestAcceptanceFailure = null;

    internal void Append(ProgressEvent progressEvent) => _timeline.Add(progressEvent);

    internal void AddTask(TaskSpec task) => _tasks.Add(task);

    internal void AddDependency(GoalId dependencyId) => _dependsOn.Add(dependencyId);

    internal bool AddEffectiveAcceptanceCriteriaCorrection(EffectiveAcceptanceCriteriaCorrection correction)
    {
        var normalizedSuperseded = RequireText(correction.SupersededCriterion, nameof(correction.SupersededCriterion));
        var normalizedCorrection = RequireText(correction.Correction, nameof(correction.Correction));
        var normalizedActor = RequireText(correction.Actor, nameof(correction.Actor));
        var normalized = correction with
        {
            SupersededCriterion = normalizedSuperseded,
            Correction = normalizedCorrection,
            Actor = normalizedActor
        };

        if (_effectiveAcceptanceCriteriaCorrections.Any(existing =>
            string.Equals(existing.SupersededCriterion, normalized.SupersededCriterion, StringComparison.Ordinal) &&
            string.Equals(existing.Correction, normalized.Correction, StringComparison.Ordinal) &&
            string.Equals(existing.Actor, normalized.Actor, StringComparison.Ordinal) &&
            existing.RecordedAt == normalized.RecordedAt &&
            existing.SourceTaskId == normalized.SourceTaskId &&
            existing.SourceKind == normalized.SourceKind))
        {
            return false;
        }

        _effectiveAcceptanceCriteriaCorrections.Add(normalized);
        return true;
    }

    internal GoalSnapshot ToSnapshot()
    {
        if (IsMetadataOnly)
        {
            return new GoalSnapshot(
                Id.Value,
                Objective,
                Status,
                [],
                [],
                IsMetadataOnly: true,
                ResultCommit: MetadataResultCommit,
                CreatedAt: MetadataCreatedAt,
                TerminatedAt: MetadataTerminatedAt);
        }

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
            SourceBacklogItemId,
            RefinedSpec is null ? null : new RefinedSpecSnapshot(
                RefinedSpec.BehavioralContract,
                RefinedSpec.AcceptanceCriteria.ToList(),
                RefinedSpec.VerificationClass.ToString(),
                RefinedSpec.Decisions.Select(d => new RefinedSpecDecisionSnapshot(d.Question, d.Choice, d.Rationale)).ToList(),
                RefinedSpec.OpenQuestions.Select(q => new RefinedSpecOpenQuestionSnapshot(
                    q.Id,
                    q.Question,
                    q.ForkKind,
                    q.Status,
                    q.Answer,
                    q.TopicKey,
                    q.NormalizedQuestionKey)).ToList()),
            LatestAcceptanceFailure is null
                ? null
                : new AcceptanceFailureSnapshot(
                    LatestAcceptanceFailure.OccurredAt,
                    LatestAcceptanceFailure.FailedChecks.ToList(),
                    LatestAcceptanceFailure.BranchHeadSha,
                    LatestAcceptanceFailure.MainHeadSha),
            _effectiveAcceptanceCriteriaCorrections.Count == 0
                ? null
                : _effectiveAcceptanceCriteriaCorrections.Select(correction => new EffectiveAcceptanceCriteriaCorrectionSnapshot(
                    correction.SupersededCriterion,
                    correction.Correction,
                    correction.Actor,
                    correction.RecordedAt,
                    correction.SourceTaskId?.Value,
                    correction.SourceKind)).ToList());
    }

    internal static Goal FromSnapshot(GoalSnapshot snapshot)
    {
        if (snapshot.IsMetadataOnly)
        {
            return CreateMetadataOnlyTerminal(new TerminalGoalMetadata(
                new GoalId(snapshot.Id),
                snapshot.Status,
                snapshot.Objective,
                snapshot.ResultCommit,
                snapshot.CreatedAt,
                snapshot.TerminatedAt));
        }

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

        if (snapshot.RefinedSpec is { } rs)
        {
            goal.SetRefinedSpec(new RefinedSpec(
                rs.BehavioralContract,
                rs.AcceptanceCriteria,
                Enum.TryParse<VerificationClass>(rs.VerificationClass, out var vc) ? vc : VerificationClass.TestVerifiable,
                rs.Decisions.Select(d => new RefinedSpecDecision(d.Question, d.Choice, d.Rationale)).ToList(),
                rs.OpenQuestions.Select(q => new RefinedSpecOpenQuestion(
                    q.Id,
                    q.Question,
                    q.ForkKind,
                    q.Status,
                    q.Answer,
                    q.TopicKey,
                    q.NormalizedQuestionKey)).ToList()));
        }

        if (snapshot.LatestAcceptanceFailure is { } failure)
        {
            goal.RecordAcceptanceFailure(
                failure.FailedChecks,
                failure.OccurredAt,
                failure.BranchHeadSha,
                failure.MainHeadSha);
        }

        foreach (var correction in snapshot.EffectiveAcceptanceCriteriaCorrections ?? [])
        {
            goal.AddEffectiveAcceptanceCriteriaCorrection(new EffectiveAcceptanceCriteriaCorrection(
                correction.SupersededCriterion,
                correction.Correction,
                correction.Actor,
                correction.RecordedAt,
                correction.SourceTaskId is null ? null : new TaskId(correction.SourceTaskId),
                correction.SourceKind));
        }

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

    private static string? NormalizeSha(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildMetadataTitle(string value)
    {
        var title = value
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = value.Trim();
        }

        return title.Length <= 240 ? title : title[..240];
    }

    private static bool IsTerminalMetadataStatus(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;
}

public sealed record AcceptanceFailureSummary(
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> FailedChecks,
    string? BranchHeadSha = null,
    string? MainHeadSha = null);
