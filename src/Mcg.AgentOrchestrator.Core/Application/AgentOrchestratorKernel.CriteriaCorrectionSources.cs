namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public void ReportWorkerTaskProgress(GoalId goalId, TaskId taskId, WorkTaskStatus status, string message) =>
        ReportTaskProgress(goalId, taskId, status, message,
            correctionSource: CriteriaCorrectionSource.WorkerResult);

    public void RecordWorkerResultCriteriaCorrection(GoalId goalId, TaskId taskId, string output) =>
        RecordIgnoredCriteriaCorrection(GetGoal(goalId), taskId, CriteriaCorrectionSource.WorkerResult, output);

    private void RecordIgnoredCriteriaCorrection(
        Goal goal,
        TaskId? taskId,
        CriteriaCorrectionSource source,
        string message)
    {
        if (!message.Replace("\r\n", "\n").Split('\n').Any(line =>
            HasRejectedCorrection(line, taskId)))
            return;

        var token = source switch
        {
            CriteriaCorrectionSource.WorkerResult => "worker-result",
            CriteriaCorrectionSource.AgentIntent => "agent-intent",
            CriteriaCorrectionSource.Escalation => "escalation",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "An operator correction cannot be ignored.")
        };
        Append(goal, taskId, ProgressKind.TaskNote,
            $"CRITERIA_CORRECTION_IGNORED source={token} task={taskId?.Value ?? "none"}");
        _eventWriter.AppendCriteriaCorrectionIgnored(goal.Id, taskId, token);
    }

    private bool HasRejectedCorrection(string line, TaskId? taskId)
    {
        var index = line.IndexOf("CRITERIA CORRECTION", StringComparison.OrdinalIgnoreCase);
        var contractIndex = line.IndexOf("CONTRACT CORRECTION", StringComparison.OrdinalIgnoreCase);
        if (index < 0 || contractIndex >= 0 && contractIndex < index)
            index = contractIndex;
        return index >= 0 && EffectiveAcceptanceCriteriaCorrectionParser.Parse(
            line[index..], CriteriaCorrectionActor, _clock.UtcNow, taskId, ProgressKind.TaskNote).Count > 0;
    }
}
