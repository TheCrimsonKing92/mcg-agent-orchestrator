namespace Mcg.AgentOrchestrator.Core;

public interface IGoalLifecycleEventWriter
{
    void AppendTimelineEvent(ProgressEvent progressEvent);
    void AppendGoalCreated(GoalId goalId, string objective);
    void AppendClarificationNeeded(GoalId goalId, string clarificationId);
    void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName);
    void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt);
    void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures);
    void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch);
    void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source);
    void AppendCleanedUp(GoalId goalId);
}

public sealed class NullGoalLifecycleEventWriter : IGoalLifecycleEventWriter
{
    public static NullGoalLifecycleEventWriter Instance { get; } = new();

    public void AppendTimelineEvent(ProgressEvent progressEvent) { }
    public void AppendGoalCreated(GoalId goalId, string objective) { }
    public void AppendClarificationNeeded(GoalId goalId, string clarificationId) { }
    public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) { }
    public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) { }
    public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) { }
    public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) { }
    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) { }
    public void AppendCleanedUp(GoalId goalId) { }
}
