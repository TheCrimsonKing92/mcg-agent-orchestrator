namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public HumanInputRequest GetHumanInputRequest(HumanInputRequestId requestId)
    {
        return _humanInputRequests.TryGetValue(requestId, out var request)
            ? request
            : throw new KeyNotFoundException($"Human input request '{requestId}' was not found.");
    }

    public TaskSpec GetTask(GoalId goalId, TaskId taskId) => GetGoal(goalId).FindTask(taskId);

    public TaskBrief BuildTaskBrief(GoalId goalId, TaskId taskId)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var pendingInput = GetPendingHumanInput(goalId)
            .Where(request => request.TaskId == taskId || request.TaskId is null)
            .ToList();
        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, task.RequiredRole);
        var timeline = PromptContextFormatter.SelectPromptTimelineEvents(
            goal.Timeline.Where(evt => evt.TaskId == taskId || evt.TaskId is null),
            maxEvents: TimelineEventBudget(complexity));

        var lines = new List<string>
        {
            "# Agent Task Brief",
            string.Empty,
            $"Goal: {PromptContextFormatter.TrimPrimaryContextBlock(goal.Objective, complexity)}",
            $"Goal status: {goal.Status}",
            $"Task: {PromptContextFormatter.TrimPrimaryContextBlock(task.Description, complexity)}",
            $"Task role: {task.RequiredRole}",
            $"Task status: {task.Status}",
            $"Task id: {task.Id}",
            string.Empty,
            "## Instructions",
            "Complete this task as the assigned SDLC role. Report concrete changes, verification evidence, blockers, and any human input required.",
            "If you cannot proceed without operator input, write a line that starts with HUMAN_INPUT: followed by the exact question.",
            "Use repository-local commands for evidence when possible. Do not mark work complete without verification.",
            "Avoid generic status summaries. Tie conclusions to repository files, command output, or cited source material.",
            "When surveying files, exclude generated output such as **/bin/**, **/obj/**, .scratch, and prototype state unless the task explicitly concerns those artifacts.",
            "Prefer the dashboard source survey or /api/source-survey as the starting repository map before broad recursive file reads.",
            string.Empty
        };

        lines.AddRange(SdlcRolePromptRequirements.Build(task.RequiredRole, complexity));
        lines.Add(string.Empty);

        if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            lines.Add("## Verification Plan");
            lines.Add(PromptContextFormatter.TrimPromptBlock(task.VerificationPlan));
            lines.Add(string.Empty);
        }

        if (pendingInput.Count > 0)
        {
            lines.Add("## Pending Human Input");
            foreach (var request in pendingInput)
            {
                lines.Add($"- {request.Id}: {PromptContextFormatter.TrimPromptBlock(request.Question)}");
            }
            lines.Add(string.Empty);
        }

        if (task.LastExecution is not null)
        {
            lines.Add("## Last Model Output");
            lines.Add(PromptContextFormatter.TrimEvidenceBlock(task.LastExecution.Output, complexity));
            lines.Add(string.Empty);
        }

        if (task.LastDispatch is not null)
        {
            lines.Add("## Last Dispatch");
            lines.Add($"Worker: {task.LastDispatch.WorkerName}");
            lines.Add($"Command: {PromptContextFormatter.TrimPromptBlock(task.LastDispatch.Command)}");
            lines.Add($"Working directory: {task.LastDispatch.WorkingDirectory}");
            lines.Add(string.Empty);
        }

        if (task.LastVerification is not null)
        {
            lines.Add("## Last Verification");
            lines.Add($"Command: {task.LastVerification.Command}");
            lines.Add($"Exit code: {task.LastVerification.ExitCode}");
            lines.Add($"Verification history count: {task.VerificationHistory.Count}");
            lines.Add($"Stdout: {PromptContextFormatter.TrimEvidenceBlock(task.LastVerification.StandardOutput, complexity)}");
            lines.Add($"Stderr: {PromptContextFormatter.TrimEvidenceBlock(task.LastVerification.StandardError, complexity)}");
            lines.Add(string.Empty);
        }

        lines.Add("## Recent Timeline");
        foreach (var evt in timeline)
        {
            lines.Add(PromptContextFormatter.FormatTimelineEvent(evt, includeTimestamp: true));
        }

        return new TaskBrief(
            goal.Id,
            task.Id,
            task.RequiredRole,
            $"{task.RequiredRole}: {PromptContextFormatter.TrimPromptTitle(task.Description)}",
            string.Join(Environment.NewLine, lines));
    }

    private static int TimelineEventBudget(TaskComplexity complexity)
    {
        return complexity == TaskComplexity.Complex ? 20 : 8;
    }
}
