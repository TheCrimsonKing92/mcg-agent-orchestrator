namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const int BriefEvidenceMaxChars = 1200;
    private const int BriefEvidenceHeadChars = 800;
    private const int BriefEvidenceTailChars = 400;

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
        var timeline = goal.Timeline
            .Where(evt => evt.TaskId == taskId || evt.TaskId is null)
            .OrderBy(evt => evt.OccurredAt)
            .TakeLast(20)
            .ToList();

        var lines = new List<string>
        {
            "# Agent Task Brief",
            string.Empty,
            $"Goal: {goal.Objective}",
            $"Goal status: {goal.Status}",
            $"Task: {task.Description}",
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

        lines.AddRange(SdlcRolePromptRequirements.Build(task.RequiredRole));
        lines.Add(string.Empty);

        if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            lines.Add("## Verification Plan");
            lines.Add(task.VerificationPlan);
            lines.Add(string.Empty);
        }

        if (pendingInput.Count > 0)
        {
            lines.Add("## Pending Human Input");
            foreach (var request in pendingInput)
            {
                lines.Add($"- {request.Id}: {request.Question}");
            }
            lines.Add(string.Empty);
        }

        if (task.LastExecution is not null)
        {
            lines.Add("## Last Model Output");
            lines.Add(TrimBriefEvidence(task.LastExecution.Output));
            lines.Add(string.Empty);
        }

        if (task.LastDispatch is not null)
        {
            lines.Add("## Last Dispatch");
            lines.Add($"Worker: {task.LastDispatch.WorkerName}");
            lines.Add($"Command: {task.LastDispatch.Command}");
            lines.Add($"Working directory: {task.LastDispatch.WorkingDirectory}");
            lines.Add(string.Empty);
        }

        if (task.LastVerification is not null)
        {
            lines.Add("## Last Verification");
            lines.Add($"Command: {task.LastVerification.Command}");
            lines.Add($"Exit code: {task.LastVerification.ExitCode}");
            lines.Add($"Verification history count: {task.VerificationHistory.Count}");
            lines.Add($"Stdout: {TrimBriefEvidence(task.LastVerification.StandardOutput)}");
            lines.Add($"Stderr: {TrimBriefEvidence(task.LastVerification.StandardError)}");
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
            $"{task.RequiredRole}: {task.Description}",
            string.Join(Environment.NewLine, lines));
    }

    private static string TrimBriefEvidence(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= BriefEvidenceMaxChars)
        {
            return trimmed;
        }

        var omitted = trimmed.Length - BriefEvidenceHeadChars - BriefEvidenceTailChars;
        return trimmed[..BriefEvidenceHeadChars] +
            $"{Environment.NewLine}...[truncated {omitted} chars for prompt budget]...{Environment.NewLine}" +
            trimmed[^BriefEvidenceTailChars..];
    }

}
