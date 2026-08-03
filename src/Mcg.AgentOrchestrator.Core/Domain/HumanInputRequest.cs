using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed class HumanInputRequest
{
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
        HumanInputRequestId? supersededByRequestId = null)
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

    public int SuppressionCount { get; private set; }

    public HumanInputRequestId? SupersededByRequestId { get; private set; }

    public bool IsCompleted { get; private set; }

    public string? Answer { get; private set; }

    public DateTimeOffset? AnsweredAt { get; private set; }

    public bool WasDismissed { get; private set; }

    public bool IsSyntheticParkedHumanWaitCompletion =>
        IsCompleted &&
        !WasDismissed &&
        Answer?.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase) == true;

    internal void Complete(string answer, DateTimeOffset answeredAt)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(answer));
        }

        IsCompleted = true;
        Answer = answer.Trim();
        AnsweredAt = answeredAt;
        WasDismissed = false;
    }

    internal void Dismiss(DateTimeOffset dismissedAt)
    {
        IsCompleted = true;
        Answer = null;
        AnsweredAt = dismissedAt;
        WasDismissed = true;
    }

    internal void IncrementSuppressionCount() => SuppressionCount++;

    internal void CompleteAsSuperseded(
        string answer,
        DateTimeOffset answeredAt,
        HumanInputRequestId answeredRequestId)
    {
        Complete(answer, answeredAt);
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
            SupersededByRequestId?.Value);
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
            snapshot.SupersededByRequestId is null ? null : new HumanInputRequestId(snapshot.SupersededByRequestId));

        if (snapshot.IsCompleted)
        {
            if (snapshot.WasDismissed)
            {
                request.Dismiss(snapshot.AnsweredAt ?? snapshot.RequestedAt);
            }
            else
            {
                request.Complete(snapshot.Answer ?? string.Empty, snapshot.AnsweredAt ?? snapshot.RequestedAt);
            }
        }

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

public sealed record HumanInputRequestCreationResult(
    HumanInputRequest Request,
    bool WasReused,
    bool WasSuppressedByAnswer)
{
    public bool WasCreated => !WasReused && !WasSuppressedByAnswer;
}

public sealed record HumanInputRequestCounts(int Total, int Open);
