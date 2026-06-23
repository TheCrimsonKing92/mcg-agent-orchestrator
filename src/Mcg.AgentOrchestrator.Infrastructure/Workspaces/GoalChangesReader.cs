using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum GoalChangesAttribution
{
    Exact,
    Uncommitted,
    Approximate
}

public sealed record GoalChangesTaskEntry(
    string TaskId,
    string TaskDescription,
    AgentRole Role,
    GoalChangesAttribution Attribution,
    IReadOnlyList<string> Files,
    string? BaseCommit,
    string? ResultCommit);

public sealed record GoalChangesReport(
    string GoalId,
    string GoalPrefix,
    string GoalObjective,
    string GoalStatus,
    IReadOnlyList<GoalChangesTaskEntry> Entries);

public static class GoalChangesReader
{
    internal static Func<string, string[], GitCli.GitResult> RunGit { get; set; } =
        (dir, args) => GitCli.Run(dir, args);

    public static GoalChangesReport Build(
        Goal goal,
        string? worktreePath,
        bool showCommitted,
        bool showWorking,
        string? roleFilter,
        string? taskFilter)
    {
        var tasksWithDispatch = goal.Tasks
            .Where(t => t.LastDispatch is not null)
            .OrderBy(t => t.LastDispatch!.DispatchedAt)
            .ToArray();

        var ordered = tasksWithDispatch.AsEnumerable();

        if (roleFilter is not null && Enum.TryParse<AgentRole>(roleFilter, ignoreCase: true, out var filterRole))
            ordered = ordered.Where(t => t.RequiredRole == filterRole);

        if (taskFilter is not null)
            ordered = ordered.Where(t => t.Id.Value.StartsWith(taskFilter, StringComparison.OrdinalIgnoreCase));

        var entries = new List<GoalChangesTaskEntry>();
        foreach (var task in ordered.OrderBy(t => SdlcRoleOrder(t.RequiredRole)).ThenBy(t => t.LastDispatch!.DispatchedAt))
        {
            var dispatch = task.LastDispatch!;

            if (dispatch.BaseCommit is not null && dispatch.ResultCommit is not null)
            {
                if (showCommitted)
                {
                    var files = worktreePath is not null
                        ? GetDiffFiles(worktreePath, dispatch.BaseCommit, dispatch.ResultCommit)
                        : [];
                    entries.Add(new GoalChangesTaskEntry(
                        task.Id.Value, task.Description, task.RequiredRole,
                        GoalChangesAttribution.Exact, files,
                        dispatch.BaseCommit, dispatch.ResultCommit));
                }
            }
            else if (task.Status == WorkTaskStatus.Running)
            {
                if (showWorking)
                {
                    var files = worktreePath is not null
                        ? GetStatusFiles(worktreePath)
                        : [];
                    entries.Add(new GoalChangesTaskEntry(
                        task.Id.Value, task.Description, task.RequiredRole,
                        GoalChangesAttribution.Uncommitted, files,
                        dispatch.BaseCommit, null));
                }
            }
            else
            {
                if (showCommitted)
                {
                    var idx = Array.FindIndex(tasksWithDispatch, t => t.Id == task.Id);
                    var since = dispatch.DispatchedAt;
                    var until = idx >= 0 && idx < tasksWithDispatch.Length - 1
                        ? tasksWithDispatch[idx + 1].LastDispatch!.DispatchedAt
                        : (DateTimeOffset?)null;
                    var files = worktreePath is not null
                        ? GetLegacyFiles(worktreePath, since, until)
                        : [];
                    entries.Add(new GoalChangesTaskEntry(
                        task.Id.Value, task.Description, task.RequiredRole,
                        GoalChangesAttribution.Approximate, files,
                        null, null));
                }
            }
        }

        return new GoalChangesReport(
            goal.Id.Value,
            goal.Id.Value.Length >= 8 ? goal.Id.Value[..8] : goal.Id.Value,
            goal.Objective,
            goal.Status.ToString(),
            entries);
    }

    private static IReadOnlyList<string> GetDiffFiles(string worktreePath, string baseCommit, string resultCommit)
    {
        var result = RunGit(worktreePath, ["diff", $"{baseCommit}..{resultCommit}", "--name-only"]);
        return result.Succeeded ? ParseFileList(result.Output) : [];
    }

    private static IReadOnlyList<string> GetStatusFiles(string worktreePath)
    {
        var result = RunGit(worktreePath, ["status", "--short"]);
        return result.ExitCode == 0 ? ParseStatusOutput(result.Output) : [];
    }

    private static IReadOnlyList<string> GetLegacyFiles(string worktreePath, DateTimeOffset since, DateTimeOffset? until)
    {
        string[] args = until.HasValue
            ? ["log", "--name-only", "--format=", $"--since={since:O}", $"--until={until.Value:O}"]
            : ["log", "--name-only", "--format=", $"--since={since:O}"];
        var result = RunGit(worktreePath, args);
        return result.Succeeded ? ParseFileList(result.Output) : [];
    }

    internal static IReadOnlyList<string> ParseFileList(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ParseStatusOutput(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line[3..].Trim() : string.Empty)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int SdlcRoleOrder(AgentRole role) => role switch
    {
        AgentRole.Planner => 0,
        AgentRole.Ideation => 1,
        AgentRole.Researcher => 2,
        AgentRole.Developer => 3,
        AgentRole.Tester => 4,
        AgentRole.Reviewer => 5,
        _ => 99
    };
}
