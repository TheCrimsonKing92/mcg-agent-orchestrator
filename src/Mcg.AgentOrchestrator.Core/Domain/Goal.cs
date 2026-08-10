namespace Mcg.AgentOrchestrator.Core;

public enum SourceBacklogCoverage { Full, Slice }

public sealed class Goal
{
    public const int OperatorAcceptanceRegateCap = 3;

    private readonly List<TaskSpec> _tasks;
    private readonly List<ProgressEvent> _timeline = [];
    private readonly List<GoalBriefVersion> _briefVersions = [];
    private readonly List<RefinedSpecVersion> _refinedSpecVersions = [];
    private readonly List<EffectiveAcceptanceCriteriaCorrection> _effectiveAcceptanceCriteriaCorrections = [];
    private readonly HashSet<GoalId> _dependsOn = [];

    public Goal(GoalId id, string objective, IReadOnlyList<TaskSpec> tasks)
        : this(id, objective, tasks, isMetadataOnly: false, DateTimeOffset.MinValue, sliceBatchParentId: null)
    {
    }

    internal Goal(
        GoalId id,
        string objective,
        IReadOnlyList<TaskSpec> tasks,
        DateTimeOffset createdAt,
        GoalId? sliceBatchParentId = null)
        : this(id, objective, tasks, isMetadataOnly: false, createdAt, sliceBatchParentId)
    {
    }

    private Goal(
        GoalId id,
        string objective,
        IReadOnlyList<TaskSpec> tasks,
        bool isMetadataOnly,
        DateTimeOffset createdAt,
        GoalId? sliceBatchParentId)
    {
        Id = id;
        SliceBatchParentId = sliceBatchParentId;
        var initialBrief = RequireText(objective, nameof(objective));
        _briefVersions.Add(new GoalBriefVersion(1, initialBrief, createdAt));
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

    public GoalId? SliceBatchParentId { get; }

    public string Objective => AuthoritativeBrief.Text;

    public GoalBriefVersion AuthoritativeBrief =>
        _briefVersions.Single(version => version.IsAuthoritative);

    public IReadOnlyList<GoalBriefVersion> BriefVersions => _briefVersions;

    public GoalStatus Status { get; private set; } = GoalStatus.Draft;

    public RefinedSpec? RefinedSpec => AuthoritativeRefinedSpecVersion?.Spec;

    public RefinedSpecVersion? AuthoritativeRefinedSpecVersion =>
        _refinedSpecVersions.LastOrDefault(version => version.IsAuthoritative);

    public IReadOnlyList<RefinedSpecVersion> RefinedSpecVersions => _refinedSpecVersions;

    public AcceptanceFailureSummary? LatestAcceptanceFailure { get; private set; }

    public int AutomaticAcceptanceRetryCount { get; private set; }

    public int OperatorAcceptanceRegateCount { get; private set; }

    public int ClarificationRoundCount { get; private set; }

    public string? SourceBacklogItemId { get; private set; }

    public SourceBacklogCoverage? SourceBacklogCoverage { get; private set; }

    public IReadOnlyList<TaskSpec> Tasks => _tasks;

    public IReadOnlyList<ProgressEvent> Timeline => _timeline;

    public IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> EffectiveAcceptanceCriteriaCorrections => _effectiveAcceptanceCriteriaCorrections;

    public IReadOnlyCollection<GoalId> DependsOn => _dependsOn;

    public bool IsMetadataOnly { get; }

    public bool IsTerminal => IsTerminalMetadataStatus(Status);

    public string? MetadataResultCommit { get; private set; }

    public GoalHoldState? CurrentHold { get; private set; }

    public DateTimeOffset? MetadataCreatedAt { get; private set; }

    public DateTimeOffset? MetadataTerminatedAt { get; private set; }

    internal static Goal CreateMetadataOnlyTerminal(TerminalGoalMetadata metadata)
    {
        if (!IsTerminalMetadataStatus(metadata.Status))
        {
            throw new ArgumentException($"Status '{metadata.Status}' is not terminal.", nameof(metadata));
        }

        var goal = new Goal(
            metadata.Id,
            BuildMetadataTitle(metadata.Title),
            [],
            isMetadataOnly: true,
            metadata.CreatedAt ?? DateTimeOffset.MinValue,
            sliceBatchParentId: null);
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

        if (IsMetadataOnly)
        {
            return new TerminalGoalMetadata(
                Id,
                Status,
                BuildMetadataTitle(Objective),
                MetadataResultCommit,
                MetadataCreatedAt,
                MetadataTerminatedAt);
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

    internal void SetRefinedSpec(RefinedSpec spec, DateTimeOffset recordedAt)
    {
        var current = AuthoritativeRefinedSpecVersion;
        if (current is null)
        {
            _refinedSpecVersions.Add(new RefinedSpecVersion(
                1,
                spec,
                recordedAt,
                AuthoritativeBrief.Version));
            return;
        }

        var currentIndex = _refinedSpecVersions.FindIndex(version => version.Version == current.Version);
        _refinedSpecVersions[currentIndex] = current with { Spec = spec };
    }

    internal RefinedSpecVersion RecordRefinedSpec(RefinedSpec spec, DateTimeOffset recordedAt)
    {
        var current = AuthoritativeRefinedSpecVersion;
        if (current is null)
        {
            var initial = new RefinedSpecVersion(1, spec, recordedAt, AuthoritativeBrief.Version);
            _refinedSpecVersions.Add(initial);
            return initial;
        }

        var nextVersion = current.Version + 1;
        var currentIndex = _refinedSpecVersions.FindIndex(version => version.Version == current.Version);
        _refinedSpecVersions[currentIndex] = current with { SupersededByVersion = nextVersion };
        var replacement = new RefinedSpecVersion(
            nextVersion,
            spec,
            recordedAt,
            AuthoritativeBrief.Version);
        _refinedSpecVersions.Add(replacement);
        return replacement;
    }

    internal void RecordClarificationRound() => ClarificationRoundCount++;

    internal void RestoreClarificationRoundCount(int count) =>
        ClarificationRoundCount = Math.Max(0, count);

    internal void SetSourceBacklogItemId(string id)
    {
        SourceBacklogItemId = RequireText(id, nameof(id));
        SourceBacklogCoverage = null;
    }

    internal void SetSourceBacklogItemLink(string id, SourceBacklogCoverage coverage)
    {
        SourceBacklogItemId = RequireText(id, nameof(id));
        SourceBacklogCoverage = coverage;
    }

    internal void RecordAcceptanceFailure(
        IReadOnlyList<string> failedChecks,
        DateTimeOffset occurredAt,
        string? branchHeadSha = null,
        string? mainHeadSha = null,
        IReadOnlyList<AcceptanceCheckAttribution>? checkAttributions = null,
        string? baselineAttestation = null)
    {
        var checks = failedChecks
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        LatestAcceptanceFailure = checks.Length == 0
            ? null
            : new AcceptanceFailureSummary(
                occurredAt,
                checks,
                NormalizeSha(branchHeadSha),
                NormalizeSha(mainHeadSha),
                checkAttributions,
                string.IsNullOrWhiteSpace(baselineAttestation) ? null : baselineAttestation.Trim());
    }

    internal void ClearAcceptanceFailure() => LatestAcceptanceFailure = null;

    internal void IncrementAutomaticAcceptanceRetryCount() => AutomaticAcceptanceRetryCount++;

    internal void ResetAutomaticAcceptanceRetryCount() => AutomaticAcceptanceRetryCount = 0;

    internal int IncrementOperatorAcceptanceRegateCount() => ++OperatorAcceptanceRegateCount;

    internal void RestoreAcceptanceRetryCounts(int automaticRetryCount, int operatorRegateCount)
    {
        AutomaticAcceptanceRetryCount = Math.Max(0, automaticRetryCount);
        OperatorAcceptanceRegateCount = Math.Max(0, operatorRegateCount);
    }

    internal GoalHoldObservation ObserveHold(
        string state,
        string blocker,
        DateTimeOffset observedAt,
        TimeSpan stallThreshold,
        string? stableIdentity = null)
    {
        state = RequireText(state, nameof(state));
        blocker = RequireText(blocker, nameof(blocker));
        if (stallThreshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stallThreshold));
        }

        var identity = GoalHoldState.BuildIdentity(state, blocker, stableIdentity);
        if (CurrentHold is null || !string.Equals(CurrentHold.Identity, identity, StringComparison.Ordinal))
        {
            CurrentHold = new GoalHoldState(identity, state, blocker, observedAt);
            return new GoalHoldObservation(CurrentHold, StateChanged: true, BecameStalled: false);
        }

        CurrentHold = CurrentHold with { Blocker = blocker };
        var repeatedFor = observedAt - CurrentHold.StartedAt;
        if (CurrentHold.StalledAt is null && repeatedFor >= stallThreshold && repeatedFor >= TimeSpan.Zero)
        {
            CurrentHold = CurrentHold with { StalledAt = observedAt };
            return new GoalHoldObservation(CurrentHold, StateChanged: true, BecameStalled: true);
        }

        return new GoalHoldObservation(CurrentHold, StateChanged: false, BecameStalled: false);
    }

    internal bool ClearHold()
    {
        if (CurrentHold is null)
        {
            return false;
        }

        CurrentHold = null;
        return true;
    }

    internal void RestoreHold(GoalHoldSnapshot? snapshot)
    {
        CurrentHold = snapshot is null
            ? null
            : new GoalHoldState(
                snapshot.Identity,
                snapshot.State,
                snapshot.Blocker,
                snapshot.StartedAt,
                snapshot.StalledAt);
    }

    internal void Append(ProgressEvent progressEvent) => _timeline.Add(progressEvent);

    internal GoalBriefVersion ReviseBrief(string newBrief, string? reason, DateTimeOffset recordedAt)
    {
        if (IsTerminal)
        {
            throw new GoalBriefRevisionNotAllowedException(
                $"Goal '{Id.Value}' is {Status} and terminal goals cannot be revised.");
        }

        if (string.IsNullOrWhiteSpace(newBrief))
        {
            throw new ArgumentException("Goal brief cannot be empty.", nameof(newBrief));
        }

        var normalizedBrief = newBrief.Trim();
        var current = AuthoritativeBrief;
        if (string.Equals(NormalizeLineEndings(current.Text), NormalizeLineEndings(normalizedBrief), StringComparison.Ordinal))
        {
            throw new GoalBriefRevisionNoChangeException(
                $"Submitted brief is identical to authoritative brief v{current.Version} after line-ending normalization.");
        }

        var nextVersionNumber = current.Version + 1;
        var currentIndex = _briefVersions.FindIndex(version => version.Version == current.Version);
        _briefVersions[currentIndex] = current with { SupersededByVersion = nextVersionNumber };
        var next = new GoalBriefVersion(
            nextVersionNumber,
            normalizedBrief,
            recordedAt,
            NormalizeOptionalText(reason));
        _briefVersions.Add(next);
        return next;
    }

    internal void AddTask(TaskSpec task) => _tasks.Add(task);

    internal void AddTaskBeforeRole(TaskSpec task, AgentRole beforeRole)
    {
        var index = _tasks.FindIndex(candidate => candidate.RequiredRole == beforeRole);
        if (index < 0)
        {
            throw new InvalidOperationException($"Cannot insert a task before missing role {beforeRole}.");
        }

        _tasks.Insert(index, task);
    }

    internal void AddDependency(GoalId dependencyId) => _dependsOn.Add(dependencyId);

    internal bool RemoveDependency(GoalId dependencyId) => _dependsOn.Remove(dependencyId);

    internal int ClearDependencies()
    {
        var count = _dependsOn.Count;
        _dependsOn.Clear();
        return count;
    }

    internal bool AddEffectiveAcceptanceCriteriaCorrection(EffectiveAcceptanceCriteriaCorrection correction)
    {
        var normalizedSuperseded = RequireText(correction.SupersededCriterion, nameof(correction.SupersededCriterion));
        var normalizedCorrection = RequireText(correction.Correction, nameof(correction.Correction));
        var normalizedActor = NormalizeSingleLine(correction.Actor, nameof(correction.Actor));
        var normalized = correction with
        {
            SupersededCriterion = normalizedSuperseded,
            Correction = normalizedCorrection,
            Actor = normalizedActor,
            CapturedAcceptanceCriteriaHash = NormalizeOptionalText(correction.CapturedAcceptanceCriteriaHash)
        };

        var duplicate = normalized.IsWaiver
            ? _effectiveAcceptanceCriteriaCorrections.Any(existing =>
                existing.IsWaiver &&
                string.Equals(existing.SupersededCriterion, normalized.SupersededCriterion, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Actor, normalized.Actor, StringComparison.OrdinalIgnoreCase))
            : _effectiveAcceptanceCriteriaCorrections.Any(existing =>
                !existing.IsWaiver &&
                string.Equals(existing.SupersededCriterion, normalized.SupersededCriterion, StringComparison.Ordinal) &&
                string.Equals(existing.Correction, normalized.Correction, StringComparison.Ordinal) &&
                string.Equals(existing.Actor, normalized.Actor, StringComparison.Ordinal) &&
                existing.RecordedAt == normalized.RecordedAt &&
                existing.SourceTaskId == normalized.SourceTaskId &&
                existing.SourceKind == normalized.SourceKind);
        if (duplicate)
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
                evt.OccurredAt,
                evt.RequeueSkipped,
                evt.OperatorGates)).ToList(),
            _dependsOn.Count > 0 ? _dependsOn.Select(id => id.Value).ToList() : null,
            SourceBacklogItemId,
            RefinedSpec is null ? null : ToRefinedSpecSnapshot(RefinedSpec),
            LatestAcceptanceFailure is null
                ? null
                : new AcceptanceFailureSnapshot(
                    LatestAcceptanceFailure.OccurredAt,
                    LatestAcceptanceFailure.FailedChecks.ToList(),
                    LatestAcceptanceFailure.BranchHeadSha,
                    LatestAcceptanceFailure.MainHeadSha,
                    LatestAcceptanceFailure.CheckAttributions?.ToList(),
                    LatestAcceptanceFailure.BaselineAttestation),
            _effectiveAcceptanceCriteriaCorrections.Count == 0
                ? null
                : _effectiveAcceptanceCriteriaCorrections.Select(correction => new EffectiveAcceptanceCriteriaCorrectionSnapshot(
                    correction.SupersededCriterion,
                    correction.Correction,
                    correction.Actor,
                    correction.RecordedAt,
                    correction.SourceTaskId?.Value,
                    correction.SourceKind,
                    correction.IsWaiver,
                    correction.CapturedAcceptanceCriteriaHash)).ToList(),
            AutomaticAcceptanceRetryCount: AutomaticAcceptanceRetryCount,
            OperatorAcceptanceRegateCount: OperatorAcceptanceRegateCount,
            CurrentHold: CurrentHold is null
                ? null
                : new GoalHoldSnapshot(
                    CurrentHold.Identity,
                    CurrentHold.State,
                    CurrentHold.Blocker,
                    CurrentHold.StartedAt,
                    CurrentHold.StalledAt),
            ClarificationRoundCount: ClarificationRoundCount,
            BriefVersions: _briefVersions.ToArray(),
            RefinedSpecVersions: _refinedSpecVersions
                .Select(version => new RefinedSpecVersionSnapshot(
                    version.Version,
                    ToRefinedSpecSnapshot(version.Spec),
                    version.RecordedAt,
                    version.BriefVersion,
                    version.SupersededByVersion))
                .ToArray(),
            SourceBacklogCoverage: SourceBacklogCoverage,
            SliceBatchParentId: SliceBatchParentId?.Value);
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

        var initialRecordedAt = snapshot.CreatedAt ??
            snapshot.Timeline.OrderBy(item => item.OccurredAt).FirstOrDefault()?.OccurredAt ??
            DateTimeOffset.MinValue;
        var goal = new Goal(
            new GoalId(snapshot.Id),
            snapshot.Objective,
            snapshot.Tasks.Select(TaskSpec.FromSnapshot).ToList(),
            initialRecordedAt,
            snapshot.SliceBatchParentId is null ? null : new GoalId(snapshot.SliceBatchParentId));
        goal.RestoreBriefVersions(snapshot.BriefVersions);
        goal.SetStatus(snapshot.Status);
        goal.RestoreClarificationRoundCount(snapshot.ClarificationRoundCount);

        foreach (var evt in snapshot.Timeline.OrderBy(item => item.OccurredAt))
        {
            goal.Append(new ProgressEvent(
                new GoalId(evt.GoalId),
                evt.TaskId is null ? null : new TaskId(evt.TaskId),
                evt.Kind,
                evt.Message,
                evt.OccurredAt,
                evt.RequeueSkipped,
                evt.OperatorGates));
        }

        foreach (var depId in snapshot.DependsOn ?? [])
        {
            goal.AddDependency(new GoalId(depId));
        }

        if (snapshot.SourceBacklogItemId is not null)
        {
            if (snapshot.SourceBacklogCoverage is { } coverage)
                goal.SetSourceBacklogItemLink(snapshot.SourceBacklogItemId, coverage);
            else
                goal.SetSourceBacklogItemId(snapshot.SourceBacklogItemId);
        }
        else if (snapshot.SourceBacklogCoverage is not null)
        {
            throw new InvalidOperationException("A source backlog coverage declaration requires a source backlog item id.");
        }

        if (snapshot.RefinedSpecVersions is { Count: > 0 } refinedSpecVersions)
        {
            goal.RestoreRefinedSpecVersions(refinedSpecVersions);
        }
        else if (snapshot.RefinedSpec is { } legacyRefinedSpec)
        {
            goal.SetRefinedSpec(FromRefinedSpecSnapshot(legacyRefinedSpec), initialRecordedAt);
        }

        if (snapshot.LatestAcceptanceFailure is { } failure)
        {
            goal.RecordAcceptanceFailure(
                failure.FailedChecks,
                failure.OccurredAt,
                failure.BranchHeadSha,
                failure.MainHeadSha,
                failure.CheckAttributions,
                failure.BaselineAttestation);
        }

        foreach (var correction in snapshot.EffectiveAcceptanceCriteriaCorrections ?? [])
        {
            goal.AddEffectiveAcceptanceCriteriaCorrection(new EffectiveAcceptanceCriteriaCorrection(
                correction.SupersededCriterion,
                correction.Correction,
                correction.Actor,
                correction.RecordedAt,
                correction.SourceTaskId is null ? null : new TaskId(correction.SourceTaskId),
                correction.SourceKind,
                correction.IsWaiver,
                correction.CapturedAcceptanceCriteriaHash));
        }

        goal.RestoreAcceptanceRetryCounts(
            snapshot.AutomaticAcceptanceRetryCount,
            snapshot.OperatorAcceptanceRegateCount);
        goal.RestoreHold(snapshot.CurrentHold);

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

    private void RestoreRefinedSpecVersions(IReadOnlyList<RefinedSpecVersionSnapshot> snapshots)
    {
        var versions = snapshots
            .OrderBy(snapshot => snapshot.Version)
            .Select(snapshot => new RefinedSpecVersion(
                snapshot.Version,
                FromRefinedSpecSnapshot(snapshot.Spec),
                snapshot.RecordedAt,
                snapshot.BriefVersion,
                snapshot.SupersededByVersion))
            .ToArray();
        if (versions.Select(version => version.Version).SequenceEqual(Enumerable.Range(1, versions.Length)) is false ||
            versions.Count(version => version.IsAuthoritative) != 1 ||
            versions[^1].IsAuthoritative is false ||
            versions.Take(versions.Length - 1).Any(version => version.SupersededByVersion != version.Version + 1))
        {
            throw new InvalidOperationException($"Goal '{Id.Value}' has an invalid refined-spec version chain.");
        }

        _refinedSpecVersions.Clear();
        _refinedSpecVersions.AddRange(versions);
    }

    private static RefinedSpecSnapshot ToRefinedSpecSnapshot(RefinedSpec spec) =>
        new(
            spec.BehavioralContract,
            spec.AcceptanceCriteria.ToList(),
            spec.VerificationClass.ToString(),
            spec.Decisions.Select(decision => new RefinedSpecDecisionSnapshot(
                decision.Question,
                decision.Choice,
                decision.Rationale)).ToList(),
            spec.OpenQuestions.Select(question => new RefinedSpecOpenQuestionSnapshot(
                question.Id,
                question.Question,
                question.ForkKind,
                question.Status,
                question.Answer,
                question.TopicKey,
                question.NormalizedQuestionKey,
                question.Criterion,
                question.BlastRadius)).ToList(),
            spec.OperatorOwnedAcceptanceCriteria.ToList(),
            spec.ClarificationAnswerHistory.ToList());

    private static RefinedSpec FromRefinedSpecSnapshot(RefinedSpecSnapshot snapshot) =>
        new(
            snapshot.BehavioralContract,
            snapshot.AcceptanceCriteria,
            Enum.TryParse<VerificationClass>(snapshot.VerificationClass, out var verificationClass)
                ? verificationClass
                : VerificationClass.TestVerifiable,
            snapshot.Decisions.Select(decision => new RefinedSpecDecision(
                decision.Question,
                decision.Choice,
                decision.Rationale)).ToList(),
            snapshot.OpenQuestions.Select(question => new RefinedSpecOpenQuestion(
                question.Id,
                question.Question,
                question.ForkKind,
                question.Status,
                question.Answer,
                question.TopicKey,
                question.NormalizedQuestionKey,
                question.Criterion,
                question.BlastRadius)).ToList())
        {
            OperatorOwnedAcceptanceCriteria = snapshot.OperatorOwnedAcceptanceCriteria ?? [],
            ClarificationAnswerHistory = snapshot.ClarificationAnswerHistory ?? []
        };

    private static string NormalizeSingleLine(string value, string parameterName) =>
        string.Join(' ', RequireText(value, parameterName)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void RestoreBriefVersions(IReadOnlyList<GoalBriefVersion>? versions)
    {
        if (versions is not { Count: > 0 })
        {
            return;
        }

        var ordered = versions.OrderBy(version => version.Version).ToArray();
        if (ordered.Length == 1 &&
            !string.Equals(ordered[0].Text, Objective, StringComparison.Ordinal))
        {
            // A single-version snapshot may have been written by a legacy scalar-only
            // mutation path. Preserve that update while keeping revised chains strict.
            ordered[0] = ordered[0] with { Text = Objective };
        }
        else if (ordered.Length > 1 &&
                 !string.Equals(ordered[^1].Text, Objective, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Goal '{Id.Value}' has divergent objective and brief-version state.");
        }

        if (ordered.Select(version => version.Version).Where((version, index) => version != index + 1).Any() ||
            ordered.Count(version => version.IsAuthoritative) != 1 ||
            !ordered[^1].IsAuthoritative)
        {
            throw new InvalidOperationException($"Goal '{Id.Value}' has an invalid brief-version chain.");
        }

        for (var index = 0; index < ordered.Length - 1; index++)
        {
            if (ordered[index].SupersededByVersion != ordered[index + 1].Version)
            {
                throw new InvalidOperationException($"Goal '{Id.Value}' has a broken brief-version successor link.");
            }
        }

        _briefVersions.Clear();
        _briefVersions.AddRange(ordered);
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

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

public enum AcceptanceFailureOrigin
{
    Inherited,
    Introduced,
    Unattributed
}

public sealed record AcceptanceCheckAttribution(
    string CheckName,
    AcceptanceFailureOrigin Origin,
    string Evidence);

public sealed record AcceptanceFailureSummary(
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> FailedChecks,
    string? BranchHeadSha = null,
    string? MainHeadSha = null,
    IReadOnlyList<AcceptanceCheckAttribution>? CheckAttributions = null,
    string? BaselineAttestation = null);
