using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed class HumanInputRequest
{
    private readonly List<OperatorGateRecord> _operatorGates = [];
    private readonly List<HumanInputAnswerRecord> _answerHistory = [];

    public HumanInputRequest(
        HumanInputRequestId id,
        GoalId goalId,
        TaskId? taskId,
        string question,
        DateTimeOffset requestedAt,
        HumanWaitKind kind = HumanWaitKind.SpecClarification,
        bool? isAutoDefaultable = null,
        bool? isDismissible = null,
        bool isAnswerRequired = true,
        bool? isExternallyBlocked = null,
        string? suggestedDefaultAnswer = null,
        string? resumeCommand = null,
        string? questionFingerprint = null,
        string? blockerFingerprint = null,
        int suppressionCount = 0,
        HumanInputRequestId? supersededByRequestId = null,
        int suppressionAnswerRevision = 0,
        long suppressionRevision = 0)
    {
        Id = id;
        GoalId = goalId;
        TaskId = taskId;
        Question = string.IsNullOrWhiteSpace(question)
            ? throw new ArgumentException("Value cannot be empty.", nameof(question))
            : question.Trim();
        RequestedAt = requestedAt;
        Kind = kind;
        IsAutoDefaultable = isAutoDefaultable ?? HumanWaitPolicyDefaults.IsAutoDefaultable(kind);
        IsDismissible = isDismissible ?? HumanWaitPolicyDefaults.IsDismissible(kind);
        IsAnswerRequired = isAnswerRequired;
        IsExternallyBlocked = isExternallyBlocked ?? HumanWaitPolicyDefaults.IsExternallyBlocked(kind);
        SuggestedDefaultAnswer = string.IsNullOrWhiteSpace(suggestedDefaultAnswer)
            ? null
            : suggestedDefaultAnswer.Trim();
        ResumeCommand = string.IsNullOrWhiteSpace(resumeCommand)
            ? BuildDefaultResumeCommand(id)
            : resumeCommand.Trim();
        QuestionFingerprint = string.IsNullOrWhiteSpace(questionFingerprint)
            ? BuildQuestionFingerprint(Question)
            : questionFingerprint.Trim();
        BlockerFingerprint = string.IsNullOrWhiteSpace(blockerFingerprint) ? null : blockerFingerprint.Trim();
        SuppressionCount = Math.Max(0, suppressionCount);
        SupersededByRequestId = supersededByRequestId;
        SuppressionAnswerRevision = Math.Max(0, suppressionAnswerRevision);
        SuppressionRevision = Math.Max(0, suppressionRevision);
    }

    public HumanInputRequestId Id { get; }

    public GoalId GoalId { get; }

    public TaskId? TaskId { get; }

    public string Question { get; }

    public DateTimeOffset RequestedAt { get; }

    public DateTimeOffset CreatedAt => RequestedAt;

    public HumanWaitKind Kind { get; }

    public bool IsAutoDefaultable { get; }

    public bool IsDismissible { get; }

    public bool IsAnswerRequired { get; }

    public bool IsExternallyBlocked { get; }

    public string? SuggestedDefaultAnswer { get; }

    public string ResumeCommand { get; }

    public string QuestionFingerprint { get; }

    public string? BlockerFingerprint { get; }

    public string? DerivedBlockerEvidence
    {
        get
        {
            const string marker = "Accompanying WORKER_RESULT blocker evidence:";
            var markerIndex = Question.IndexOf(marker, StringComparison.Ordinal);
            return markerIndex < 0
                ? null
                : Question[(markerIndex + marker.Length)..].Trim();
        }
    }

    public int SuppressionCount { get; private set; }

    public int SuppressionAnswerRevision { get; private set; }

    public long SuppressionRevision { get; private set; }

    public HumanInputRequestId? SupersededByRequestId { get; private set; }

    public bool IsCompleted { get; private set; }

    public string? Answer => AuthoritativeAnswer?.Text;

    public DateTimeOffset? AnsweredAt => AuthoritativeAnswer?.AnsweredAt ?? DismissedAt;

    public HumanInputAnswerRecord? AuthoritativeAnswer =>
        _answerHistory.LastOrDefault(answer => !answer.IsRetracted);

    public IReadOnlyList<HumanInputAnswerRecord> AnswerHistory => _answerHistory;

    public IReadOnlyList<OperatorGateRecord> OperatorGates => _operatorGates;

    public bool WasDismissed { get; private set; }

    public bool IsSyntheticParkedHumanWaitCompletion =>
        IsCompleted &&
        !WasDismissed &&
        Answer?.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase) == true;

    internal void Complete(
        string answer,
        DateTimeOffset answeredAt,
        IReadOnlyList<string>? gatedDeliverableIds = null,
        HumanInputAnswerOrigin origin = HumanInputAnswerOrigin.Operator,
        int briefVersion = 1)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(answer));
        }

        IsCompleted = true;
        _answerHistory.Clear();
        _answerHistory.Add(new HumanInputAnswerRecord(
            $"{Id.Value}:answer:0",
            answer.Trim(),
            answeredAt,
            origin,
            BriefVersion: briefVersion));
        WasDismissed = false;
        ResetSuppressionCountForAnswerRevision();
        foreach (var deliverableId in gatedDeliverableIds ?? [])
        {
            var normalized = deliverableId.Trim();
            if (normalized.Length > 0 && !_operatorGates.Any(gate =>
                    string.Equals(gate.DeliverableId, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                _operatorGates.Add(new OperatorGateRecord(
                    normalized,
                    $"clarification:{Id.Value}",
                    answeredAt));
            }
        }
    }

    internal void Dismiss(DateTimeOffset dismissedAt)
    {
        IsCompleted = true;
        _answerHistory.Clear();
        DismissedAt = dismissedAt;
        WasDismissed = true;
    }

    private DateTimeOffset? DismissedAt { get; set; }

    internal HumanInputAnswerRecord Supersede(
        string answer,
        DateTimeOffset answeredAt,
        HumanInputAnswerOrigin origin,
        int briefVersion = 1)
    {
        if (origin != HumanInputAnswerOrigin.Operator)
        {
            throw new UnauthorizedAccessException("Only operator / human-input origin may supersede a clarification answer.");
        }

        if (!IsCompleted || WasDismissed || AuthoritativeAnswer is null)
        {
            throw new InvalidOperationException($"Human input request '{Id}' has no answered clarification to supersede.");
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(answer));
        }

        var answerId = $"{Id.Value}:answer:{_answerHistory.Count}";
        var currentIndex = _answerHistory.FindLastIndex(candidate => !candidate.IsRetracted);
        var current = _answerHistory[currentIndex];
        _answerHistory[currentIndex] = current with { SupersededByAnswerId = answerId };
        var replacement = new HumanInputAnswerRecord(
            answerId,
            answer.Trim(),
            answeredAt,
            origin,
            BriefVersion: briefVersion);
        _answerHistory.Add(replacement);
        ResetSuppressionCountForAnswerRevision();
        return replacement;
    }

    internal void IncrementSuppressionCount()
    {
        AlignSuppressionAnswerRevision();
        SuppressionCount++;
        SuppressionRevision++;
    }

    internal void ResetSuppressionCount()
    {
        AlignSuppressionAnswerRevision();
        if (SuppressionCount == 0)
        {
            return;
        }

        SuppressionCount = 0;
        SuppressionRevision++;
    }

    private void ResetSuppressionCountForAnswerRevision()
    {
        SuppressionCount = 0;
        SuppressionAnswerRevision = _answerHistory.Count;
        SuppressionRevision = 0;
    }

    private void AlignSuppressionAnswerRevision()
    {
        var answerRevision = _answerHistory.Count;
        if (SuppressionAnswerRevision == answerRevision)
        {
            return;
        }

        SuppressionCount = 0;
        SuppressionAnswerRevision = answerRevision;
        SuppressionRevision = 0;
    }

    internal OperatorGateRecord MarkOperatorGateSatisfied(
        string deliverableId,
        string evidence,
        DateTimeOffset satisfiedAt)
    {
        var index = _operatorGates.FindIndex(gate =>
            gate.IsActive && string.Equals(gate.DeliverableId, deliverableId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new KeyNotFoundException($"Active operator gate '{deliverableId}' was not found on clarification '{Id.Value[..8]}'.");
        }

        _operatorGates[index] = _operatorGates[index] with
        {
            SatisfiedAt = satisfiedAt,
            SatisfactionEvidence = string.IsNullOrWhiteSpace(evidence)
                ? throw new ArgumentException("Gate satisfaction evidence cannot be empty.", nameof(evidence))
                : evidence.Trim()
        };
        return _operatorGates[index];
    }

    internal void CompleteAsSuperseded(
        string answer,
        DateTimeOffset answeredAt,
        HumanInputRequestId answeredRequestId,
        int briefVersion = 1)
    {
        Complete(answer, answeredAt, briefVersion: briefVersion);
        SupersededByRequestId = answeredRequestId;
    }

    internal HumanInputRequestSnapshot ToSnapshot()
    {
        return new HumanInputRequestSnapshot(
            Id.Value,
            GoalId.Value,
            TaskId?.Value,
            Question,
            RequestedAt,
            Kind,
            IsAutoDefaultable,
            IsDismissible,
            IsAnswerRequired,
            IsExternallyBlocked,
            SuggestedDefaultAnswer,
            ResumeCommand,
            IsCompleted,
            Answer,
            AnsweredAt,
            WasDismissed,
            QuestionFingerprint,
            BlockerFingerprint,
            SuppressionCount,
            SupersededByRequestId?.Value,
            _operatorGates.Count == 0 ? null : _operatorGates.ToArray(),
            _answerHistory.Count == 0 ? null : _answerHistory.ToArray(),
            SuppressionAnswerRevision,
            SuppressionRevision);
    }

    internal static HumanInputRequest FromSnapshot(HumanInputRequestSnapshot snapshot)
    {
        var request = new HumanInputRequest(
            new HumanInputRequestId(snapshot.Id),
            new GoalId(snapshot.GoalId),
            snapshot.TaskId is null ? null : new TaskId(snapshot.TaskId),
            snapshot.Question,
            snapshot.RequestedAt,
            snapshot.Kind,
            snapshot.IsAutoDefaultable,
            snapshot.IsDismissible,
            snapshot.IsAnswerRequired,
            snapshot.IsExternallyBlocked,
            snapshot.SuggestedDefaultAnswer,
            snapshot.ResumeCommand,
            snapshot.QuestionFingerprint,
            snapshot.BlockerFingerprint,
            snapshot.SuppressionCount,
            snapshot.SupersededByRequestId is null ? null : new HumanInputRequestId(snapshot.SupersededByRequestId),
            snapshot.SuppressionAnswerRevision,
            snapshot.SuppressionRevision);

        if (snapshot.IsCompleted)
        {
            if (snapshot.WasDismissed)
            {
                request.Dismiss(snapshot.AnsweredAt ?? snapshot.RequestedAt);
            }
            else if (snapshot.AnswerHistory is { Count: > 0 })
            {
                request.IsCompleted = true;
                request.WasDismissed = false;
                request._answerHistory.AddRange(snapshot.AnswerHistory.OrderBy(answer => answer.AnsweredAt));
            }
            else
            {
                request.Complete(snapshot.Answer ?? string.Empty, snapshot.AnsweredAt ?? snapshot.RequestedAt);
            }
        }

        request._operatorGates.Clear();
        request._operatorGates.AddRange(snapshot.OperatorGates ?? []);

        return request;
    }

    public static string BuildDefaultResumeCommand(HumanInputRequestId id) => $"answer {id.Value[..8]} <answer>";

    public static string BuildQuestionFingerprint(string question) => BuildFingerprint(Normalize(question));

    public static string BuildWorkerResultBlockerFingerprint(
        TaskId taskId,
        AgentRole role,
        string question,
        string? blocker)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return BuildFingerprint(string.Join(
            "\n",
            taskId.Value,
            role.ToString(),
            Normalize(question),
            Normalize(blocker ?? string.Empty)));
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();

    private static string BuildFingerprint(string normalizedValue) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedValue))).ToLowerInvariant();
}

/// <summary>Records an answer and, when known, the authoritative brief version that governed it.</summary>
/// <param name="BriefVersion">
/// The authoritative brief version at write time, or <see langword="null"/> when the persistence caller
/// does not have authoritative goal context. The store never fabricates provenance.
/// </param>
public sealed record HumanInputAnswerRecord(
    string Id,
    string Text,
    DateTimeOffset AnsweredAt,
    HumanInputAnswerOrigin Origin = HumanInputAnswerOrigin.Operator,
    string? SupersededByAnswerId = null,
    int? BriefVersion = null)
{
    public bool IsRetracted => SupersededByAnswerId is not null;
}

public sealed record OperatorGateRecord(
    string DeliverableId,
    string SourceRecordId,
    DateTimeOffset RecordedAt,
    DateTimeOffset? SatisfiedAt = null,
    string? SatisfactionEvidence = null)
{
    public bool IsActive => SatisfiedAt is null;
}

public sealed record HumanInputRequestCreationResult(
    HumanInputRequest Request,
    bool WasReused,
    bool WasSuppressedByAnswer)
{
    public bool WasCreated => !WasReused && !WasSuppressedByAnswer;
}

public sealed record HumanInputRequestCounts(int Total, int Open);
