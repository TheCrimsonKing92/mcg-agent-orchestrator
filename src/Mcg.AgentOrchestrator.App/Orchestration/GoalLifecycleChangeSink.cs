using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalLifecycleChangeSink
{
    internal static Action<GoalLifecycleCommit> Create(OrchestratorWorkspace workspace)
    {
        var writer = new ChangeStreamWriter(Path.Combine(workspace.LogDirectory, ChangeStreamWriter.FileName));
        return commit =>
        {
            if (!commit.HasOperatorIntent && commit.EventType is not ("TaskDispatched" or "TaskCompleted" or
                "TaskVerified" or "TaskFailed" or "AcceptanceResult" or "HumanInputReceived" or "HumanInputSuperseded")) return;

            var detail = commit.EventType;
            if (commit.TaskId is not null) detail += $" task={commit.TaskId}";
            if (commit.Role is not null) detail += $" role={commit.Role}";
            writer.Append("goal-lifecycle", commit.GoalId.Value, detail, commit.Timestamp);
        };
    }
}
