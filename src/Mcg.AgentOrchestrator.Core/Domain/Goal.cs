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
    private readonly List<CriterionEvidenceObligation> _criterionEvidenceObligations = [];
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

    private AcceptanceFailureSummary? _retainedAcceptanceFailure;
    private bool _acceptanceFailureDeferredForRetry;

    public AcceptanceFailureSummary? LatestAcceptanceFailure =>
        _acceptanceFailureDeferredForRetry ? null : _retainedAcceptanceFailure;

    public AcceptanceFailureSummary? RetainedAcceptanceFailure => _retainedAcceptanceFailure;

    public int AutomaticAcceptanceRetryCount { get; private set; }

    public int OperatorAcceptanceRegateCount { get; private set; }

    public int ClarificationRoundCount { get; private set; }

    public string? SourceBacklogItemId { get; private set; }

    public SourceBacklogCoverage? SourceBacklogCoverage { get; private set; }

    public IReadOnlyList<TaskSpec> Tasks => _tasks;

    public IReadOnlyList<ProgressEvent> Timeline => _timeline;

    public ProgressEvent? LatestTaskRetryAfterAcceptanceFailure(TaskId taskId)
    {
        if (RetainedAcceptanceFailure is not { } failure)
        {
            return null;
        }

        return _timeline
            .Where(evt =>
                evt.TaskId == taskId &&
                evt.Kind == ProgressKind.TaskRetried &&
                evt.OccurredAt >= failure.OccurredAt)
            .OrderByDescending(evt => evt.OccurredAt)
            .FirstOrDefault();
    }

    public IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> EffectiveAcceptanceCriteriaCorrections => _effectiveAcceptanceCriteriaCorrections;

    // The invariant is a goal-level outstanding proof obligation, not a task
    // result. Only refinement or an attributed operator action may set Owner.
    public IReadOnlyList<CriterionEvidenceObligation> CriterionEvidenceObligations => _criterionEvidenceObligations;

    public IReadOnlyList<CriterionEvidenceObligation> OutstandingCriterionEvidenceObligations =>
        _criterionEvidenceObligations.Where(obligation =>
            obligation.State is not (CriterionEvidenceState.Satisfied or CriterionEvidenceState.Repaired) &&
            IsCurrentCriterionEvidenceObligation(obligation)).ToArray();

    public IReadOnlyList<CriterionEvidenceObligation> GetOutstandingCriterionEvidenceObligations(string? candidateSha) =>
        _criterionEvidenceObligations.Where(obligation =>
            obligation.State != CriterionEvidenceState.Repaired &&
            IsCurrentCriterionEvidenceObligation(obligation) && !obligation.HasSatisfiedEvidenceFor(candidateSha)).ToArray();

    private bool IsCurrentCriterionEvidenceObligation(CriterionEvidenceObligation obligation) =>
        obligation.Owner == CriterionEvidenceOwner.Unknown ||
        !_refinedSpecVersions.Any(version => version.Version == obligation.CriterionVersion && version.IsSuperseded);

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
            EnsureCriterionEvidenceObligations(spec, criterionVersion: 1, recordedAt);
            return;
        }

        var currentIndex = _refinedSpecVersions.FindIndex(version => version.Version == current.Version);
        _refinedSpecVersions[currentIndex] = current with { Spec = spec };
        EnsureCriterionEvidenceObligations(spec, current.Version, recordedAt);
    }

    internal void RestoreCriterionEvidenceObligations(IReadOnlyList<CriterionEvidenceObligation>? obligations)
    {
        _criterionEvidenceObligations.Clear();
        foreach (var obligation in obligations ?? [])
        {
            if (!IsValidCriterionEvidenceObligation(obligation) ||
                _criterionEvidenceObligations.Any(existing => string.Equals(existing.Id, obligation.Id, StringComparison.Ordinal)))
            {
                _criterionEvidenceObligations.Add(CreateMalformedCriterionEvidenceObligation(obligation));
                continue;
            }

            _criterionEvidenceObligations.Add(obligation);
        }

        // Persisted receipts supplement the spec's obligations; a missing row
        // cannot remove a proof requirement during restore.
        foreach (var version in _refinedSpecVersions)
        {
            EnsureCriterionEvidenceObligations(version.Spec, version.Version, version.RecordedAt);
        }

        ValidateRepairedCriterionEvidenceObligationTargets();
    }

    private bool IsValidCriterionEvidenceObligation(CriterionEvidenceObligation? obligation)
    {
        if (obligation is null) return false;
        // A repaired malformed claim deliberately retains its original, possibly
        // non-criterion-shaped identity as an audited history record.  Its
        // replacement is the only live evidence obligation.
        if (obligation.Owner == CriterionEvidenceOwner.Unknown &&
            obligation.State == CriterionEvidenceState.Repaired)
        {
            return !string.IsNullOrWhiteSpace(obligation.Id) &&
                !string.IsNullOrWhiteSpace(obligation.RequiredScope) &&
                !string.IsNullOrWhiteSpace(obligation.Provenance) &&
                obligation.CandidateSha is null && obligation.ReceiptId is null && obligation.Detail is null &&
                !string.IsNullOrWhiteSpace(obligation.ReplacementObligationId) &&
                obligation.ReplacementOwner is CriterionEvidenceOwner.Acceptance or CriterionEvidenceOwner.Operator &&
                !string.Equals(obligation.Id, obligation.ReplacementObligationId, StringComparison.Ordinal);
        }
        var version = _refinedSpecVersions.SingleOrDefault(item => item.Version == obligation.CriterionVersion);
        return version is not null && obligation.CriterionIndex >= 0 &&
            obligation.CriterionIndex < version.Spec.AcceptanceCriteria.Count &&
            obligation.Id == CriterionEvidenceObligation.BuildId(obligation.CriterionVersion, obligation.CriterionIndex) &&
            string.Equals(obligation.Criterion, version.Spec.AcceptanceCriteria[obligation.CriterionIndex].Trim(), StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(obligation.RequiredScope) &&
            !string.IsNullOrWhiteSpace(obligation.Provenance) && obligation.HasValidEvidenceState;
    }

    private CriterionEvidenceObligation CreateMalformedCriterionEvidenceObligation(CriterionEvidenceObligation? malformed)
    {
        // Retain the claimed identity for diagnosis even when it cannot be
        // resolved. Unknown/Pending prevents treating the claim as authority.
        var criterionVersion = malformed?.CriterionVersion ?? 0;
        var criterionIndex = malformed?.CriterionIndex ?? -1;
        var version = _refinedSpecVersions.SingleOrDefault(item => item.Version == criterionVersion);
        var canRecoverIdentity = version is not null &&
            criterionIndex >= 0 && criterionIndex < version.Spec.AcceptanceCriteria.Count;
        var id = canRecoverIdentity
            ? CriterionEvidenceObligation.BuildId(criterionVersion, criterionIndex)
            : $"malformed-persisted-{_criterionEvidenceObligations.Count}";
        while (_criterionEvidenceObligations.Any(existing => string.Equals(existing.Id, id, StringComparison.Ordinal)))
        {
            id = $"{id}-duplicate";
        }

        return new CriterionEvidenceObligation(
            id,
            criterionIndex,
            criterionVersion,
            canRecoverIdentity
                ? version!.Spec.AcceptanceCriteria[criterionIndex]
                : "Malformed persisted criterion evidence obligation",
            CriterionEvidenceOwner.Unknown,
            CriterionEvidenceState.Pending,
            "ownership mapping required",
            "malformed persisted obligation; operator mapping or explicit audited correction required",
            malformed?.RecordedAt ?? DateTimeOffset.UnixEpoch);
    }

    private void ValidateRepairedCriterionEvidenceObligationTargets()
    {
        for (var index = 0; index < _criterionEvidenceObligations.Count; index++)
        {
            var repaired = _criterionEvidenceObligations[index];
            if (repaired.State != CriterionEvidenceState.Repaired)
                continue;

            var target = _criterionEvidenceObligations.SingleOrDefault(item =>
                string.Equals(item.Id, repaired.ReplacementObligationId, StringComparison.Ordinal));
            if (target is not null && target.Owner == repaired.ReplacementOwner &&
                target.State != CriterionEvidenceState.Repaired && IsValidCriterionEvidenceObligation(target))
                continue;

            _criterionEvidenceObligations[index] = repaired with
            {
                Owner = CriterionEvidenceOwner.Unknown,
                State = CriterionEvidenceState.Pending,
                RequiredScope = "ownership mapping required",
                Provenance = $"{repaired.Provenance}; repair target '{repaired.ReplacementObligationId ?? "missing"}' is absent or incompatible; explicit operator remapping required",
                CandidateSha = null,
                ReceiptId = null,
                Detail = null,
                ExpectedCandidateSha = null,
                ReplacementObligationId = null,
                ReplacementOwner = null
            };
        }
    }

    internal CriterionEvidenceObligation MapCriterionEvidenceOwner(
        int criterionIndex,
        int criterionVersion,
        CriterionEvidenceOwner owner,
        string actor,
        DateTimeOffset recordedAt,
        string? requiredScope = null,
        string? findingStableId = null,
        string? expectedCandidateSha = null)
    {
        if (owner is not (CriterionEvidenceOwner.Acceptance or CriterionEvidenceOwner.Operator))
            throw new ArgumentOutOfRangeException(nameof(owner), "Only Acceptance or Operator may be assigned by an operator mapping.");
        if (string.IsNullOrWhiteSpace(expectedCandidateSha))
            throw new ArgumentException("An operator mapping must bind the obligation to the current candidate SHA.", nameof(expectedCandidateSha));

        var specVersion = _refinedSpecVersions.SingleOrDefault(version => version.Version == criterionVersion)
            ?? throw new InvalidOperationException($"Criterion version {criterionVersion} is not present on goal '{Id.Value}'.");
        if (specVersion.IsSuperseded)
            throw new InvalidOperationException($"Criterion version {criterionVersion} is superseded and cannot receive a new ownership mapping.");
        if (criterionIndex < 0 || criterionIndex >= specVersion.Spec.AcceptanceCriteria.Count)
            throw new ArgumentOutOfRangeException(nameof(criterionIndex), "Criterion index is not present in the requested version.");

        var id = CriterionEvidenceObligation.BuildId(criterionVersion, criterionIndex);
        var existingIndex = _criterionEvidenceObligations.FindIndex(item => item.Id == id);
        var normalizedScope = RequireText(
            requiredScope ?? (owner == CriterionEvidenceOwner.Acceptance
                ? CriterionEvidenceScopes.FullAcceptanceGate
                : "operator observation"),
            nameof(requiredScope));
        if (owner == CriterionEvidenceOwner.Acceptance &&
            !string.Equals(normalizedScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Acceptance-owned evidence must require '{CriterionEvidenceScopes.FullAcceptanceGate}', the only scope the deterministic acceptance executor can prove.",
                nameof(requiredScope));
        }

        var mapped = new CriterionEvidenceObligation(
            id,
            criterionIndex,
            criterionVersion,
            RequireText(specVersion.Spec.AcceptanceCriteria[criterionIndex], nameof(criterionIndex)),
            owner,
            existingIndex >= 0 ? _criterionEvidenceObligations[existingIndex].State : CriterionEvidenceState.Pending,
            normalizedScope,
            $"operator mapping by {NormalizeSingleLine(actor, nameof(actor))}",
            recordedAt,
            FindingStableId: string.IsNullOrWhiteSpace(findingStableId)
                ? null
                : NormalizeSingleLine(findingStableId, nameof(findingStableId)),
            CandidateSha: existingIndex >= 0 ? _criterionEvidenceObligations[existingIndex].CandidateSha : null,
            ReceiptId: existingIndex >= 0 ? _criterionEvidenceObligations[existingIndex].ReceiptId : null,
            Detail: existingIndex >= 0 ? _criterionEvidenceObligations[existingIndex].Detail : null,
            ExpectedCandidateSha: NormalizeSha(expectedCandidateSha));
        if (existingIndex < 0)
        {
            _criterionEvidenceObligations.Add(mapped);
            return mapped;
        }

        var existing = _criterionEvidenceObligations[existingIndex];
        if (existing.Owner == owner &&
            string.Equals(existing.RequiredScope, mapped.RequiredScope, StringComparison.Ordinal) &&
            string.Equals(existing.ExpectedCandidateSha, mapped.ExpectedCandidateSha, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.FindingStableId, mapped.FindingStableId, StringComparison.Ordinal))
            return existing;

        if (existing.State == CriterionEvidenceState.Satisfied &&
            string.Equals(existing.CandidateSha, mapped.ExpectedCandidateSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Criterion obligation '{id}' is already satisfied for this candidate and cannot be re-owned.");

        mapped = mapped with
        {
            State = CriterionEvidenceState.Pending,
            CandidateSha = null,
            ReceiptId = null,
            Detail = null,
            PriorReceipts = existing.ArchiveCurrentReceipt()
        };
        _criterionEvidenceObligations[existingIndex] = mapped;
        return mapped;
    }

    internal CriterionEvidenceObligation RecordCriterionEvidence(
        string obligationId,
        CriterionEvidenceOwner owner,
        string candidateSha,
        string receiptId,
        string scope,
        bool passed,
        string detail,
        DateTimeOffset recordedAt)
    {
        var index = _criterionEvidenceObligations.FindIndex(item => item.Id == obligationId);
        if (index < 0)
            throw new KeyNotFoundException($"Criterion evidence obligation '{obligationId}' was not found.");

        var existing = _criterionEvidenceObligations[index];
        if (owner is not (CriterionEvidenceOwner.Acceptance or CriterionEvidenceOwner.Operator) ||
            existing.Owner != owner ||
            (existing.Owner == CriterionEvidenceOwner.Acceptance && string.IsNullOrWhiteSpace(existing.ExpectedCandidateSha)) ||
            (!string.IsNullOrWhiteSpace(existing.ExpectedCandidateSha) &&
             !string.Equals(existing.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(existing.RequiredScope, RequireText(scope, nameof(scope)), StringComparison.Ordinal))
            throw new InvalidOperationException($"Evidence receipt is incompatible with obligation '{obligationId}'.");

        candidateSha = RequireText(candidateSha, nameof(candidateSha));
        receiptId = RequireText(receiptId, nameof(receiptId));
        detail = RequireText(detail, nameof(detail));
        var incoming = new CriterionEvidenceReceipt(owner, candidateSha, receiptId, scope.Trim(), passed,
            detail, existing.Provenance, recordedAt);
        if (existing.FindReceipt(receiptId) is { } recorded)
        {
            if (recorded.HasSamePayload(incoming)) return existing;
            throw new InvalidOperationException($"Receipt '{receiptId}' already identifies different evidence for obligation '{obligationId}'.");
        }
        if (existing.State == CriterionEvidenceState.Satisfied)
        {
            throw new InvalidOperationException($"Criterion obligation '{obligationId}' is already satisfied by an incompatible receipt.");
        }

        var updated = existing with
        {
            State = passed ? CriterionEvidenceState.Satisfied : CriterionEvidenceState.Failed,
            CandidateSha = candidateSha,
            ReceiptId = receiptId,
            Detail = detail,
            RecordedAt = recordedAt,
            PriorReceipts = existing.ArchiveCurrentReceipt()
        };
        _criterionEvidenceObligations[index] = updated;
        return updated;
    }

    internal CriterionEvidenceObligation RepairMalformedCriterionEvidenceObligation(
        string malformedObligationId,
        int criterionIndex,
        int criterionVersion,
        CriterionEvidenceOwner owner,
        string actor,
        string reason,
        DateTimeOffset recordedAt,
        string? requiredScope = null,
        string? findingStableId = null,
        string? expectedCandidateSha = null)
    {
        var sourceIndex = _criterionEvidenceObligations.FindIndex(item => item.Id == malformedObligationId);
        if (sourceIndex < 0)
            throw new KeyNotFoundException($"Malformed criterion evidence obligation '{malformedObligationId}' was not found.");

        var source = _criterionEvidenceObligations[sourceIndex];
        if (source.Owner != CriterionEvidenceOwner.Unknown || source.State != CriterionEvidenceState.Pending)
            throw new InvalidOperationException($"Criterion evidence obligation '{malformedObligationId}' is not an unresolved malformed claim.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A malformed obligation repair reason is required.", nameof(reason));

        var replacement = MapCriterionEvidenceOwner(
            criterionIndex, criterionVersion, owner, actor, recordedAt, requiredScope, findingStableId, expectedCandidateSha);
        var repairProvenance = $"repaired by {NormalizeSingleLine(actor, nameof(actor))} at {recordedAt:u}; reason={NormalizeSingleLine(reason, nameof(reason))}";
        if (string.Equals(source.Id, replacement.Id, StringComparison.Ordinal))
        {
            var replacementIndex = _criterionEvidenceObligations.FindIndex(item => item.Id == replacement.Id);
            var storedReplacement = replacement with { Provenance = $"{replacement.Provenance}; {repairProvenance}" };
            _criterionEvidenceObligations[replacementIndex] = storedReplacement;
            return storedReplacement;
        }

        _criterionEvidenceObligations[sourceIndex] = source with
        {
            State = CriterionEvidenceState.Repaired,
            Provenance = $"{source.Provenance}; {repairProvenance}",
            Detail = null,
            ReplacementObligationId = replacement.Id,
            ReplacementOwner = replacement.Owner
        };
        return replacement;
    }

    private void EnsureCriterionEvidenceObligations(RefinedSpec spec, int criterionVersion, DateTimeOffset recordedAt)
    {
        for (var index = 0; index < spec.AcceptanceCriteria.Count; index++)
        {
            var id = CriterionEvidenceObligation.BuildId(criterionVersion, index);
            if (_criterionEvidenceObligations.Any(existing => existing.Id == id))
            {
                continue;
            }

            var criterion = RequireText(spec.AcceptanceCriteria[index], nameof(spec));
            var ownershipEntries = spec.OperatorOwnedAcceptanceCriteria.Count(item =>
                string.Equals(item.Trim(), criterion, StringComparison.Ordinal));
            if (ownershipEntries == 0)
            {
                // Worker verification stays on task verification records. This
                // collection represents only proof that survives worker scope.
                continue;
            }

            _criterionEvidenceObligations.Add(new CriterionEvidenceObligation(
                id,
                index,
                criterionVersion,
                criterion,
                ownershipEntries == 1 ? CriterionEvidenceOwner.Operator : CriterionEvidenceOwner.Unknown,
                CriterionEvidenceState.Pending,
                ownershipEntries == 1 ? "operator observation" : "ownership mapping required",
                ownershipEntries == 1 ? "authoritative refined spec" : "ambiguous authoritative refined spec ownership",
                recordedAt));
        }
    }

    internal RefinedSpecVersion RecordRefinedSpec(RefinedSpec spec, DateTimeOffset recordedAt)
    {
        var current = AuthoritativeRefinedSpecVersion;
        if (current is null)
        {
            var initial = new RefinedSpecVersion(1, spec, recordedAt, AuthoritativeBrief.Version);
            _refinedSpecVersions.Add(initial);
            EnsureCriterionEvidenceObligations(spec, initial.Version, recordedAt);
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
        PreserveExplicitMappingsAcrossEquivalentRefinement(current, replacement, recordedAt);
        EnsureCriterionEvidenceObligations(spec, replacement.Version, recordedAt);
        return replacement;
    }

    private void PreserveExplicitMappingsAcrossEquivalentRefinement(
        RefinedSpecVersion superseded,
        RefinedSpecVersion replacement,
        DateTimeOffset recordedAt)
    {
        foreach (var existing in _criterionEvidenceObligations
                     .Where(item => item.CriterionVersion == superseded.Version &&
                                    item.Owner is CriterionEvidenceOwner.Acceptance or CriterionEvidenceOwner.Operator &&
                                    item.Provenance.StartsWith("operator mapping by ", StringComparison.Ordinal))
                     .ToArray())
        {
            var matches = replacement.Spec.AcceptanceCriteria
                .Select((criterion, index) => (criterion: criterion.Trim(), index))
                .Where(item => string.Equals(item.criterion, existing.Criterion, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                MarkExplicitMappingUnresolved(existing, replacement, recordedAt);
                continue;
            }

            var target = matches[0];
            var targetId = CriterionEvidenceObligation.BuildId(replacement.Version, target.index);
            if (_criterionEvidenceObligations.Any(item => item.Id == targetId))
            {
                MarkExplicitMappingUnresolved(existing, replacement, recordedAt);
                continue;
            }

            _criterionEvidenceObligations.Add(existing with
            {
                Id = targetId,
                CriterionIndex = target.index,
                CriterionVersion = replacement.Version,
                Criterion = target.criterion,
                State = CriterionEvidenceState.Pending,
                CandidateSha = null,
                ReceiptId = null,
                Detail = null,
                PriorReceipts = existing.ArchiveCurrentReceipt(),
                Provenance = $"{existing.Provenance}; carried forward from criterion-v{superseded.Version}-{existing.CriterionIndex}",
                RecordedAt = recordedAt
            });
        }
    }

    private void MarkExplicitMappingUnresolved(
        CriterionEvidenceObligation existing,
        RefinedSpecVersion replacement,
        DateTimeOffset recordedAt)
    {
        var existingIndex = _criterionEvidenceObligations.FindIndex(item => item.Id == existing.Id);
        _criterionEvidenceObligations[existingIndex] = existing with
        {
            Owner = CriterionEvidenceOwner.Unknown,
            State = CriterionEvidenceState.Pending,
            RequiredScope = "ownership mapping required",
            Provenance = $"{existing.Provenance}; unresolved during refinement v{replacement.Version}; explicit operator remapping required",
            CandidateSha = null,
            ReceiptId = null,
            Detail = null,
            ExpectedCandidateSha = null,
            PriorReceipts = existing.ArchiveCurrentReceipt()
        };
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
        _retainedAcceptanceFailure = checks.Length == 0
            ? null
            : new AcceptanceFailureSummary(
                occurredAt,
                checks,
                NormalizeSha(branchHeadSha),
                NormalizeSha(mainHeadSha),
                checkAttributions,
                string.IsNullOrWhiteSpace(baselineAttestation) ? null : baselineAttestation.Trim());
        _acceptanceFailureDeferredForRetry = false;
    }

    internal void DeferAcceptanceFailureForRetry() =>
        _acceptanceFailureDeferredForRetry = _retainedAcceptanceFailure is not null;

    internal void RestoreAcceptanceFailureAfterRetryCancellation() =>
        _acceptanceFailureDeferredForRetry = false;

    internal void ClearAcceptanceFailure()
    {
        _retainedAcceptanceFailure = null;
        _acceptanceFailureDeferredForRetry = false;
    }

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
            RetainedAcceptanceFailure is null
                ? null
                : new AcceptanceFailureSnapshot(
                    RetainedAcceptanceFailure.OccurredAt,
                    RetainedAcceptanceFailure.FailedChecks.ToList(),
                    RetainedAcceptanceFailure.BranchHeadSha,
                    RetainedAcceptanceFailure.MainHeadSha,
                    RetainedAcceptanceFailure.CheckAttributions?.ToList(),
                    RetainedAcceptanceFailure.BaselineAttestation),
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
            SliceBatchParentId: SliceBatchParentId?.Value,
            AcceptanceFailureDeferredForRetry: _acceptanceFailureDeferredForRetry,
            CriterionEvidenceObligations: _criterionEvidenceObligations.Count == 0 ? null : _criterionEvidenceObligations.ToArray());
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

        goal.RestoreCriterionEvidenceObligations(snapshot.CriterionEvidenceObligations);

        if (snapshot.LatestAcceptanceFailure is { } failure)
        {
            goal.RecordAcceptanceFailure(
                failure.FailedChecks,
                failure.OccurredAt,
                failure.BranchHeadSha,
                failure.MainHeadSha,
                failure.CheckAttributions,
                failure.BaselineAttestation);
            var acceptanceFailureDeferredForRetry = snapshot.AcceptanceFailureDeferredForRetry ??
                (snapshot.Status == GoalStatus.Active &&
                 goal.Tasks.Any(task =>
                     task.LatestRetryAt is not null &&
                     task.Status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned or WorkTaskStatus.Running));
            if (acceptanceFailureDeferredForRetry)
            {
                goal.DeferAcceptanceFailureForRetry();
            }
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

public enum AcceptanceFailureCause
{
    NotClassified,
    EnvironmentalApparatus,
    FixturePublication
}

public sealed record AcceptanceCheckAttribution(
    string CheckName,
    AcceptanceFailureOrigin Origin,
    string Evidence,
    AcceptanceFailureCause Cause = AcceptanceFailureCause.NotClassified);

public sealed record AcceptanceFailureSummary(
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> FailedChecks,
    string? BranchHeadSha = null,
    string? MainHeadSha = null,
    IReadOnlyList<AcceptanceCheckAttribution>? CheckAttributions = null,
    string? BaselineAttestation = null)
{
    public bool IsEnvironmentalApparatus =>
        FailedChecks.Count > 0 &&
        CheckAttributions is { Count: > 0 } attributions &&
        FailedChecks.All(check => attributions.Any(attribution =>
            attribution.CheckName.Equals(check, StringComparison.Ordinal) &&
            attribution.Cause == AcceptanceFailureCause.EnvironmentalApparatus));
}
