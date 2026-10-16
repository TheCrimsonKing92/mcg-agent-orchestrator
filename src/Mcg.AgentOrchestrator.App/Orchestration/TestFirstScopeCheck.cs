using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the two Developer scopes; git supplies evidence, Evaluate makes the decision.
internal static class TestFirstScopeCheck
{
    internal enum CheckpointKind { Author, Implementer }
    internal enum ScopeOutcome { Passed, Failed, Skipped }

    internal static bool IsTestFirst(Goal goal) => goal.Timeline
        .LastOrDefault(entry => entry.Kind == ProgressKind.GoalPolicyDecision &&
            entry.Message.StartsWith("Intake pipeline decision (", StringComparison.Ordinal))
        ?.Message.StartsWith("Intake pipeline decision (override): test-first;", StringComparison.Ordinal) == true;

    internal static (TaskSpec Developer, CheckpointKind Kind)? SelectCheckpoint(Goal goal)
    {
        if (!IsTestFirst(goal)) return null;
        var developers = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer).ToArray();
        if (developers.Length < 2) return null;
        var checkpoint = goal.Tasks.FirstOrDefault(task => task.Status == WorkTaskStatus.Assigned &&
            goal.Tasks.TakeWhile(earlier => earlier.Id != task.Id).All(earlier => earlier.Status == WorkTaskStatus.Completed));
        if (checkpoint is null || (checkpoint.Id != developers[^1].Id && checkpoint.RequiredRole != AgentRole.Reviewer))
            return null;
        var developer = goal.Tasks.TakeWhile(task => task.Id != checkpoint.Id)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Developer && task.Status == WorkTaskStatus.Completed);
        return developer is null ? null : (developer, developer.Id == developers[0].Id ? CheckpointKind.Author : CheckpointKind.Implementer);
    }

    internal static bool IsRepeat(Goal goal, TaskId taskId) => goal.Timeline.Any(entry =>
        entry.Kind == ProgressKind.TaskNote && entry.TaskId == taskId &&
        entry.Message.StartsWith("test-first scope check failed:", StringComparison.Ordinal));

    internal static TestFirstScopeResult Evaluate(
        CheckpointKind kind,
        IReadOnlyList<(int? Added, int? Removed, string Path)> authorChanges,
        IReadOnlyList<string> implementerTouchedAuthoredPaths)
    {
        var offending = kind switch
        {
            CheckpointKind.Author => authorChanges
                .Where(row => !row.Path.StartsWith("tests/", StringComparison.Ordinal) &&
                    !(row.Path.StartsWith("src/", StringComparison.Ordinal) && row.Removed == 0))
                .Select(row => row.Path),
            CheckpointKind.Implementer => implementerTouchedAuthoredPaths,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown test-first checkpoint.")
        };
        var paths = offending.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(paths.Length == 0 ? ScopeOutcome.Passed : ScopeOutcome.Failed,
            paths.Length == 0 ? $"{kind} scope is clean" : $"{kind} scope violation: {string.Join(", ", paths)}",
            paths, authorChanges, implementerTouchedAuthoredPaths);
    }

    internal static TestFirstScopeResult Read(string worktreePath, string integrationBranch, string authorTaskId)
    {
        var step = "author commit log";
        try
        {
            string Git(params string[] args)
            {
                var result = GitCli.Run(worktreePath, args);
                if (!result.Succeeded || result.DrainTimedOut || !result.ProcessStarted)
                    throw new IOException($"exit={result.ExitCode}; drain-timeout={result.DrainTimedOut}; {result.Error.Trim()}");
                return result.Output;
            }

            var log = Git("log", "--no-merges", "--format=%H%x1f%(trailers:key=Task-Id,valueonly,separator=%x1f)%x1e",
                $"{integrationBranch}..HEAD");
            var authorCommit = log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim('\r', '\n').Split('\x1f'))
                .FirstOrDefault(parts => parts.Skip(1).Any(value => value.Trim().Equals(authorTaskId, StringComparison.Ordinal)))?[0];
            if (authorCommit is null)
                return TestFirstScopeResult.Skip($"no commit with Task-Id trailer {authorTaskId} on {integrationBranch}..HEAD");

            step = "author merge-base";
            var mergeBase = Git("merge-base", integrationBranch, authorCommit).Trim();
            step = "author numstat";
            var changes = Git("diff", "--numstat", "--no-renames", "-z", mergeBase, authorCommit, "--")
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(row =>
                {
                    var fields = row.Split('\t', 3);
                    if (fields.Length != 3) throw new FormatException("malformed numstat row");
                    return (Added: Count(fields[0]), Removed: Count(fields[1]), Path: fields[2]);
                }).ToArray();
            var authoredPaths = changes.Where(row => row.Path.StartsWith("tests/", StringComparison.Ordinal))
                .Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            step = "implementer authored-path diff";
            var touches = authoredPaths.Count == 0 ? [] : Git("diff", "--name-only", "--no-renames", "-z",
                    authorCommit, "HEAD", "--", "tests/")
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(authoredPaths.Contains).Order(StringComparer.Ordinal).ToArray();
            return new(ScopeOutcome.Passed, "git scope evidence read", [], changes, touches);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return TestFirstScopeResult.Skip($"{step}: {exception.Message}");
        }

        static int? Count(string value) => value == "-" ? null : int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}

internal sealed record TestFirstScopeResult(
    TestFirstScopeCheck.ScopeOutcome Outcome,
    string Message,
    IReadOnlyList<string> OffendingPaths,
    IReadOnlyList<(int? Added, int? Removed, string Path)> AuthorChanges,
    IReadOnlyList<string> ImplementerTouchedAuthoredPaths)
{
    internal static TestFirstScopeResult Skip(string reason) => new(TestFirstScopeCheck.ScopeOutcome.Skipped, reason, [], [], []);

    internal PolicyDecisionRecord RepeatedDecision(TaskId taskId) => new("test-first-scope", "escalate", 2,
        $"task={taskId.Value}; previous scope failure and current offending paths={string.Join(", ", OffendingPaths)}",
        $"TEST_FIRST_SCOPE_REPEATED task={taskId.Value}; {Message}",
        [new("task-id", taskId.Value), new("scope-outcome", Outcome.ToString())]);
}
