using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum AcceptanceMergeGuardMismatchKind
{
    GoalStatus,
    TaskSet,
    TaskRequiredRole,
    TaskStatus,
    WorktreeHead
}

internal sealed record AcceptanceMergeGuardTask(
    string Id,
    AgentRole RequiredRole,
    WorkTaskStatus Status);

internal sealed record AcceptanceMergeGuardSnapshot(
    GoalStatus GoalStatus,
    IReadOnlyList<AcceptanceMergeGuardTask> Tasks);

internal sealed record AcceptanceMergeGuardMismatch(
    AcceptanceMergeGuardMismatchKind Kind,
    string Field,
    string Expected,
    string Actual)
{
    public string Describe() => $"{Field} expected {Expected}, actual {Actual}";
}

internal static class AcceptanceMergeGuard
{
    private const int TaskDiagnosticLimit = 5;

    public static AcceptanceMergeGuardSnapshot Capture(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var goal = kernel.ExportSnapshot().Goals.FirstOrDefault(candidate => candidate.Id == goalId.Value)
            ?? throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists.");
        return new AcceptanceMergeGuardSnapshot(
            goal.Status,
            goal.Tasks
                .OrderBy(task => task.Id, StringComparer.Ordinal)
                .Select(task => new AcceptanceMergeGuardTask(task.Id, task.RequiredRole, task.Status))
                .ToArray());
    }

    public static AcceptanceMergeGuardMismatch? Compare(
        AcceptanceMergeGuardSnapshot expected,
        AcceptanceMergeGuardSnapshot actual)
    {
        if (expected.GoalStatus != actual.GoalStatus)
        {
            return new AcceptanceMergeGuardMismatch(
                AcceptanceMergeGuardMismatchKind.GoalStatus,
                "Goal.Status",
                expected.GoalStatus.ToString(),
                actual.GoalStatus.ToString());
        }

        var expectedById = expected.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var actualById = actual.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var added = actualById.Keys.Except(expectedById.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var removed = expectedById.Keys.Except(actualById.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (added.Length > 0 || removed.Length > 0)
        {
            return new AcceptanceMergeGuardMismatch(
                AcceptanceMergeGuardMismatchKind.TaskSet,
                "Tasks added/removed",
                $"added [] removed [{FormatTaskIds(removed)}]",
                $"added [{FormatTaskIds(added)}] removed []");
        }

        foreach (var expectedTask in expected.Tasks)
        {
            var actualTask = actualById[expectedTask.Id];
            if (expectedTask.RequiredRole != actualTask.RequiredRole)
            {
                return new AcceptanceMergeGuardMismatch(
                    AcceptanceMergeGuardMismatchKind.TaskRequiredRole,
                    $"Task {expectedTask.Id}.RequiredRole",
                    expectedTask.RequiredRole.ToString(),
                    actualTask.RequiredRole.ToString());
            }

            if (expectedTask.Status != actualTask.Status)
            {
                return new AcceptanceMergeGuardMismatch(
                    AcceptanceMergeGuardMismatchKind.TaskStatus,
                    $"Task {expectedTask.Id}.Status",
                    expectedTask.Status.ToString(),
                    actualTask.Status.ToString());
            }
        }

        return null;
    }

    public static AcceptanceMergeGuardMismatch? CompareWorktreeHead(string? expected, string? actual) =>
        string.Equals(Normalize(expected), Normalize(actual), StringComparison.OrdinalIgnoreCase)
            ? null
            : new AcceptanceMergeGuardMismatch(
                AcceptanceMergeGuardMismatchKind.WorktreeHead,
                "WorktreeHead",
                FormatValue(expected),
                FormatValue(actual));

    public static string BuildAbortMessage(
        GoalId goalId,
        AcceptanceMergeGuardMismatch mismatch,
        bool verificationPassed) =>
        $"Goal '{goalId.Value[..8]}' acceptance merge aborted: {mismatch.Describe()}. " +
        (verificationPassed
            ? "The passing verification was retained; quiesce conductor mutations for this goal before the next landing attempt."
            : "Acceptance stopped before verification; quiesce conductor mutations for this goal before trying again.");

    private static string FormatTaskIds(IReadOnlyList<string> taskIds)
    {
        var values = taskIds.Take(TaskDiagnosticLimit).Select(id => id[..Math.Min(8, id.Length)]).ToArray();
        return string.Join(',', values) + (taskIds.Count > TaskDiagnosticLimit ? $",+{taskIds.Count - TaskDiagnosticLimit} more" : string.Empty);
    }

    private static string FormatValue(string? value) => string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
