namespace Mcg.AgentOrchestrator.Core;

public sealed class HumanInputRequest
{
    public HumanInputRequest(HumanInputRequestId id, GoalId goalId, TaskId? taskId, string question, DateTimeOffset requestedAt)
    {
        Id = id;
        GoalId = goalId;
        TaskId = taskId;
        Question = string.IsNullOrWhiteSpace(question)
            ? throw new ArgumentException("Value cannot be empty.", nameof(question))
            : question.Trim();
        RequestedAt = requestedAt;
    }

    public HumanInputRequestId Id { get; }

    public GoalId GoalId { get; }

    public TaskId? TaskId { get; }

    public string Question { get; }

    public DateTimeOffset RequestedAt { get; }

    public bool IsCompleted { get; private set; }

    public string? Answer { get; private set; }

    public DateTimeOffset? AnsweredAt { get; private set; }

    internal void Complete(string answer, DateTimeOffset answeredAt)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(answer));
        }

        IsCompleted = true;
        Answer = answer.Trim();
        AnsweredAt = answeredAt;
    }

    internal HumanInputRequestSnapshot ToSnapshot()
    {
        return new HumanInputRequestSnapshot(
            Id.Value,
            GoalId.Value,
            TaskId?.Value,
            Question,
            RequestedAt,
            IsCompleted,
            Answer,
            AnsweredAt);
    }

    internal static HumanInputRequest FromSnapshot(HumanInputRequestSnapshot snapshot)
    {
        var request = new HumanInputRequest(
            new HumanInputRequestId(snapshot.Id),
            new GoalId(snapshot.GoalId),
            snapshot.TaskId is null ? null : new TaskId(snapshot.TaskId),
            snapshot.Question,
            snapshot.RequestedAt);

        if (snapshot.IsCompleted)
        {
            request.Complete(snapshot.Answer ?? string.Empty, snapshot.AnsweredAt ?? snapshot.RequestedAt);
        }

        return request;
    }
}
