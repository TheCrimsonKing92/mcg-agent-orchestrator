using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalBranchFacts(
    bool IsAcceptedOrVerifiedGitGoal,
    bool IsCompletedGitGoal,
    bool HasRegisteredWorktree,
    bool HasGoalBranch,
    bool HasGoalBranchArtifact,
    bool BranchAlreadyLanded)
{
    public bool MissingBranchOrWorktree => IsAcceptedOrVerifiedGitGoal && (!HasRegisteredWorktree || !HasGoalBranch);
}

internal sealed class GoalGitFactIndex(
    string executionDirectory,
    bool isGitWorkTree,
    IReadOnlyDictionary<string, string> goalBranchTips,
    IReadOnlySet<string> mergedGoalBranches,
    IReadOnlySet<string> registeredWorktreePaths)
{
    internal static Func<string, IReadOnlyList<string>, GitCli.GitResult> GitRunner { get; set; } =
        (workingDirectory, args) => GitCli.Run(workingDirectory, args.ToArray());

    public static GoalGitFactIndex Build(string executionDirectory)
    {
        var fullExecutionDirectory = Path.GetFullPath(executionDirectory);
        var branchResult = RunGit(fullExecutionDirectory, "for-each-ref", "--format=%(refname:short) %(objectname)", "refs/heads/goal/");
        if (branchResult.ExitCode != 0)
        {
            return new GoalGitFactIndex(fullExecutionDirectory, false, EmptyTipMap(), EmptySet(), EmptyPathSet());
        }

        var mergedResult = RunGit(fullExecutionDirectory, "for-each-ref", "--format=%(refname:short)", "--merged", "HEAD", "refs/heads/goal/");
        var worktreeResult = RunGit(fullExecutionDirectory, "worktree", "list", "--porcelain");

        return new GoalGitFactIndex(
            fullExecutionDirectory,
            true,
            ParseBranchTips(branchResult.Output),
            mergedResult.ExitCode == 0 ? ParseLines(mergedResult.Output) : EmptySet(),
            worktreeResult.ExitCode == 0 ? ParseWorktreePaths(worktreeResult.Output) : EmptyPathSet());
    }

    public bool HasGoalBranch(string branchName) => isGitWorkTree && goalBranchTips.ContainsKey(branchName);

    public GoalBranchFacts BuildGoalBranchFacts(Goal goal)
    {
        var branch = GoalWorktrees.BranchName(goal.Id);
        var isAcceptedOrVerifiedGitGoal = (goal.Status is GoalStatus.Verified or GoalStatus.Completed) && isGitWorkTree;
        var hasRegisteredWorktree = isAcceptedOrVerifiedGitGoal &&
            registeredWorktreePaths.Contains(NormalizePath(GoalWorktrees.WorktreePath(executionDirectory, goal.Id)));
        var hasGoalBranch = isAcceptedOrVerifiedGitGoal && goalBranchTips.ContainsKey(branch);
        var hasGoalBranchArtifact = hasRegisteredWorktree || hasGoalBranch;
        var branchAlreadyLanded = isAcceptedOrVerifiedGitGoal &&
            hasGoalBranchArtifact &&
            (!hasGoalBranch || mergedGoalBranches.Contains(branch));

        return new GoalBranchFacts(
            isAcceptedOrVerifiedGitGoal,
            goal.Status == GoalStatus.Completed && isGitWorkTree,
            hasRegisteredWorktree,
            hasGoalBranch,
            hasGoalBranchArtifact,
            branchAlreadyLanded);
    }

    public string BuildGoalEvidenceKey(Goal goal)
    {
        var branch = GoalWorktrees.BranchName(goal.Id);
        var hasTip = goalBranchTips.TryGetValue(branch, out var tip);
        var registeredWorktree = registeredWorktreePaths.Contains(NormalizePath(GoalWorktrees.WorktreePath(executionDirectory, goal.Id)));
        var merged = mergedGoalBranches.Contains(branch);
        return string.Join(
            "|",
            $"git={(isGitWorkTree ? "available" : "unavailable")}",
            $"branch={branch}",
            $"tip={(hasTip ? tip : "absent")}",
            $"worktree={(registeredWorktree ? "present" : "absent")}",
            $"merged={(merged ? "true" : "false")}");
    }

    private static GitCli.GitResult RunGit(string executionDirectory, params string[] args) =>
        GitRunner(executionDirectory, args);

    internal static IReadOnlySet<string> ParseLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> ParseBranchTips(string output)
    {
        var tips = EmptyTipMap();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                IsSingleToken(parts[0]) &&
                IsSingleToken(parts[1]))
            {
                tips[parts[0]] = parts[1];
            }
        }

        return tips;
    }

    internal static HashSet<string> ParseWorktreePaths(string output)
    {
        var paths = EmptyPathSet();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                paths.Add(NormalizePath(line["worktree ".Length..]));
            }
        }

        return paths;
    }

    private static bool IsSingleToken(string value) =>
        value.Length > 0 && !value.Any(char.IsWhiteSpace);

    private static HashSet<string> EmptySet() =>
        new(StringComparer.Ordinal);

    private static Dictionary<string, string> EmptyTipMap() =>
        new(StringComparer.Ordinal);

    private static HashSet<string> EmptyPathSet() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
