using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns one integration round per candidate head. The journal is authoritative; the git marker
// only allows the dispatcher to recognize the merge that this round has actually started.
internal sealed class ConductorConflictIntegration(
    AgentOrchestratorKernel kernel, string executionDirectory, string integrationBranch,
    Func<GoalId, TaskId, string, RetryRoundKind?, RetryCause, TaskSpec> retry,
    Func<Goal, DeveloperBranchIntegrationResult> inner,
    Func<string, OrchestratorBuildCheckResult>? buildCheck = null,
    Func<string, string?>? objectiveReader = null)
{
    internal const string OperationName = "conductor:conflict-integration";
    private sealed record Round(string OriginalHead, string MainSha, string[] ConflictPaths,
        string DeveloperTaskId, string[] LandingGoalPrefixes, DateTimeOffset? RetryAt = null, bool Admitted = false);

    internal (bool Started, string Detail) TryRecord(Goal goal)
    {
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        if (worktree is null || !Directory.Exists(worktree)) return (false, "worktree missing");
        var status = GitCli.InspectWorktreeStatus(worktree);
        if (!status.Succeeded) return (false, "worktree status unavailable: " + status.Error);
        if (status.IsDirty) return (false, "dirty worktree");
        if (ReadRef(worktree, "MERGE_HEAD") is not null) return (false, "merge already in progress");
        var attached = GitCli.Run(worktree, "symbolic-ref", "--quiet", "HEAD");
        if (!attached.Succeeded || attached.DrainTimedOut || attached.Output.Trim() != "refs/heads/" + GoalWorktrees.BranchName(goal.Id))
            return (false, "detached or unexpected branch");
        var original = Run(worktree, "rev-parse", "HEAD").Trim();
        var entries = GoalOperationJournal.Read(executionDirectory, goal.Id).Entries.Where(entry => entry.Operation == OperationName).ToArray();
        if (entries.Any(entry => ReadRound(entry).OriginalHead == original))
            return (false, "repeat conflict on unchanged candidate");
        var developer = goal.Tasks.LastOrDefault(task => task.RequiredRole == AgentRole.Developer && task.Status == WorkTaskStatus.Completed);
        if (developer is null) return (false, "no completed Developer task");
        if (!goal.Tasks.SkipWhile(task => task.Id != developer.Id).Skip(1).Any(task => task.RequiredRole == AgentRole.Tester))
            return (false, "no Tester task after completed Developer");
        var main = Run(worktree, "rev-parse", integrationBranch + "^{commit}").Trim();
        var tree = new WorkerGitContext().ReadReviewerMergeTreeStatus(worktree, main, original);
        if (tree.Truncated) return (false, "conflict path set truncated");
        if (tree.IsClean)
        {
            Run(worktree, [.. Identity, "merge", "--no-ff", "--no-edit", "-m",
                $"Integrate {integrationBranch} into {GoalWorktrees.BranchName(goal.Id)} before acceptance", main]);
            ResolvedCandidateObligationRebinder.Rebind(kernel, goal, Run(worktree, "rev-parse", "HEAD").Trim());
            return (true, "merged main cleanly");
        }
        var paths = tree.ConflictPaths.Order(StringComparer.Ordinal).ToArray();
        var note = BuildNote(worktree, original, main, paths, objectiveReader ?? ReadObjective, out var prefixes);
        var round = new Round(original, main, paths, developer.Id.Value, prefixes);
        Save(goal, round, GoalOperationStatus.Begin);
        try
        {
            var task = retry(goal.Id, developer.Id, note, RetryRoundKind.Standard, RetryCause.MainDriftConflict);
            Save(goal, round with { RetryAt = task.LatestRetryAt }, GoalOperationStatus.Begin);
            return (true, "conductor integration recorded; Developer retried for " + string.Join(", ", paths));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Save(goal, round, GoalOperationStatus.Failed);
            return (false, "Developer retry failed: " + exception.Message);
        }
    }

    internal DeveloperBranchIntegrationResult IntegrateMainBeforeDeveloperDispatch(Goal goal)
    {
        var round = OpenRound(goal);
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var task = goal.Tasks.FirstOrDefault(item => item.RequiredRole == AgentRole.Developer && item.Status == WorkTaskStatus.Assigned);
        if (round is null || worktree is null || task is null || task.Id.Value != round.DeveloperTaskId ||
            task.PendingRetryCause != RetryCause.MainDriftConflict || round.RetryAt is null || task.LatestRetryAt != round.RetryAt ||
            ReadRef(worktree, "HEAD") != round.OriginalHead || ReadRef(worktree, integrationBranch) != round.MainSha)
            return inner(goal);
        if (ReadRef(worktree, "MERGE_HEAD") == round.MainSha)
            return Admit(goal, task, worktree, round);
        var result = inner(goal);
        if (result.Status != DeveloperBranchIntegrationStatus.Conflict || !SamePaths(result.ConflictPaths, round.ConflictPaths))
            return result;
        // Recheck after the inner guard: an inner integration or a moved ref cannot authorize this merge.
        if (ReadRef(worktree, "HEAD") != round.OriginalHead || ReadRef(worktree, integrationBranch) != round.MainSha || GitCli.IsWorktreeDirty(worktree))
            return result;
        var merge = GitCli.Run(worktree, [.. Identity, "-c", "merge.conflictStyle=diff3", "merge", "--no-ff", "--no-commit", round.MainSha]);
        var paths = Run(worktree, "diff", "--name-only", "--diff-filter=U", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (!merge.ProcessStarted || merge.DrainTimedOut || merge.ExitCode != 1 || ReadRef(worktree, "MERGE_HEAD") != round.MainSha || !SamePaths(paths, round.ConflictPaths))
        {
            if (ReadRef(worktree, "MERGE_HEAD") is not null) Run(worktree, "merge", "--abort");
            if (ReadRef(worktree, "HEAD") != round.OriginalHead || GitCli.IsWorktreeDirty(worktree))
                throw new InvalidOperationException("Conductor integration admission failed to restore the candidate.");
            return result;
        }
        return Admit(goal, task, worktree, round);
    }

    internal string? Validate(Goal goal)
    {
        var round = OpenRound(goal);
        if (round is null || !round.Admitted) return null;
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id)
            ?? throw new InvalidOperationException("Admitted integration worktree disappeared.");
        var candidate = Run(worktree, "rev-parse", "HEAD").Trim();
        string? failure;
        try { failure = ValidateCandidate(worktree, round, candidate); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { failure = "validation unavailable: " + exception.Message; }
        if (failure is null)
        {
            Save(goal, round, GoalOperationStatus.Completed);
            ConductorConflictIntegrationMarker.Remove(worktree);
            return null;
        }
        var restored = Restore(worktree, goal, round, candidate);
        ConductorConflictIntegrationMarker.Remove(worktree);
        Save(goal, round, GoalOperationStatus.Failed);
        return $"pre-landing rebase conflict ({string.Join(", ", round.ConflictPaths)}); conductor integration failed: {failure}; " +
            (restored ? $"branch restored to {round.OriginalHead[..12]}" : "compare-and-swap restore failed; operator recovery required") +
            "; the operator integrates main into the goal branch";
    }

    private string? ValidateCandidate(string worktree, Round round, string head)
    {
        if (ReadRef(worktree, "MERGE_HEAD") is not null) return "merge still in progress";
        Run(worktree, "merge-base", "--is-ancestor", round.OriginalHead, head);
        var commits = Run(worktree, "rev-list", "--first-parent", "--reverse", round.OriginalHead + ".." + head)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (commits.Length == 0) return "merge parent check: no resolution commit";
        var parents = Run(worktree, "rev-list", "--parents", "-n", "1", commits[0]).Trim().Split(' ');
        if (parents.Length != 3 || parents[1] != round.OriginalHead || parents[2] != round.MainSha)
            return "merge parent check: expected exactly original head and recorded main";
        var mergeTree = GitCli.Run(worktree, "merge-tree", "--write-tree", round.OriginalHead, round.MainSha);
        var tree = mergeTree.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (mergeTree.DrainTimedOut || mergeTree.ExitCode is not (0 or 1) || tree is null || tree.Length != 40 || !tree.All(Uri.IsHexDigit))
            return "scope check: could not read original merge tree";
        var outside = Run(worktree, "diff", "--name-only", "-z", tree, head).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Except(round.ConflictPaths, StringComparer.Ordinal).ToArray();
        if (outside.Length > 0) return "scope check: changed outside recorded paths: " + string.Join(", ", outside);
        if (GitCli.IsWorktreeDirty(worktree)) return "resolution worktree is dirty";
        foreach (var path in round.ConflictPaths)
        {
            // A resolved deletion has no blob to scan.
            if (Run(worktree, "ls-tree", head, "--", path).Length == 0) continue;
            var lines = Run(worktree, "show", head + ":" + path).ReplaceLineEndings("\n").Split('\n');
            if (lines.Any(line => line.StartsWith("<<<<<<< ", StringComparison.Ordinal) || line.StartsWith("||||||| ", StringComparison.Ordinal) ||
                line.StartsWith(">>>>>>> ", StringComparison.Ordinal) || line == "======="))
                return "conflict marker check: " + path;
        }
        var build = buildCheck is null ? RatchetConflictDispatchIntegration.BuildChangedProjects(worktree, round.OriginalHead, head) : buildCheck(worktree);
        if (!build.Ran || build.ExitCode != 0) return "build check: " + VerificationTextBounds.BoundText(build.Output, path: null);
        return ReadRef(worktree, "HEAD") == head ? null : "candidate moved during build check";
    }

    private DeveloperBranchIntegrationResult Admit(Goal goal, TaskSpec task, string worktree, Round round)
    {
        ConductorConflictIntegrationMarker.Write(task, worktree, round.OriginalHead, round.MainSha);
        Save(goal, round with { Admitted = true }, GoalOperationStatus.Begin);
        return new(DeveloperBranchIntegrationStatus.ConflictAdmitted, "Conductor admitted integration merge; edit only " + string.Join(", ", round.ConflictPaths),
            round.ConflictPaths, round.OriginalHead, round.MainSha, round.OriginalHead);
    }

    private Round? OpenRound(Goal goal)
    {
        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var entry = journal.LatestByOperation.SingleOrDefault(item => item.Operation == OperationName);
        if (entry?.Status != GoalOperationStatus.Begin) return null;
        var round = ReadRound(entry);
        return journal.Entries.Any(item => item.Operation == OperationName &&
            item.Status is GoalOperationStatus.Completed or GoalOperationStatus.Failed && ReadRound(item).OriginalHead == round.OriginalHead)
            ? null : round;
    }

    private static Round ReadRound(GoalOperationJournalEntry entry) => JsonSerializer.Deserialize<Round>(entry.Detail ?? "null")
        ?? throw new InvalidOperationException("Malformed conductor integration journal record.");

    private void Save(Goal goal, Round round, GoalOperationStatus status)
    {
        var detail = JsonSerializer.Serialize(round);
        if (status == GoalOperationStatus.Begin) GoalOperationJournal.Begin(executionDirectory, goal, OperationName, detail, round.MainSha);
        else if (status == GoalOperationStatus.Completed) GoalOperationJournal.Completed(executionDirectory, goal, OperationName, detail, round.MainSha);
        else GoalOperationJournal.Failed(executionDirectory, goal, OperationName, detail, round.MainSha);
    }

    private string? ReadObjective(string prefix)
    {
        var matches = kernel.Goals.Where(goal => goal.Id.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Objective : null;
    }

    private static string BuildNote(string worktree, string original, string main, string[] paths,
        Func<string, string?> readObjective, out string[] prefixes)
    {
        var note = new StringBuilder();
        var goals = new HashSet<string>(StringComparer.Ordinal);
        var mergeBase = Run(worktree, "merge-base", original, main).Trim();
        var total = 0;
        foreach (var path in paths)
        {
            note.AppendLine("Conflicted path: " + path);
            var commits = Run(worktree, "log", "--reverse", "--format=%H", mergeBase + ".." + main, "--", path)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var pathLines = 0;
            var omitted = 0;
            foreach (var commit in commits)
            {
                var message = Run(worktree, "log", "-1", "--format=%B", commit);
                note.AppendLine("Main commit " + commit + ": " + message.Trim());
                var match = Regex.Match(message, @"(?m)^Integrate goal/([0-9a-fA-F]{8})\b");
                if (match.Success)
                {
                    var prefix = match.Groups[1].Value;
                    goals.Add(prefix);
                    var intent = readObjective(prefix) ?? Regex.Match(message, @"(?m)^Goal: (.+)$").Groups[1].Value.Trim();
                    note.AppendLine("Landing goal " + prefix + " objective: " + intent[..Math.Min(4000, intent.Length)]);
                }
                var diff = Run(worktree, "diff", commit + "^", commit, "--", path).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
                foreach (var line in diff)
                {
                    if (pathLines < 200 && total < 600) { note.AppendLine(line); pathLines++; total++; }
                    else omitted++;
                }
            }
            if (omitted > 0) note.AppendLine($"{omitted} more lines omitted");
        }
        prefixes = goals.Order(StringComparer.Ordinal).ToArray();
        note.Append("The conductor has started merging main into this branch; the files below are left with diff3 conflict markers: ")
            .Append(string.Join(", ", paths)).Append(". Edit only these files. In each conflict block, write code that keeps the behavior this goal's brief needs and the behavior the landing goal above added. Before finishing, open each listed file and confirm no line starts with <<<<<<<, |||||||, ======= or >>>>>>>. Leave the merge for the commit to complete. If the result changes any other file, leaves a marker, or fails the build check, the conductor resets the branch and escalates.");
        return note.ToString();
    }

    private static bool Restore(string worktree, Goal goal, Round round, string candidate)
    {
        if (ReadRef(worktree, "MERGE_HEAD") is not null)
        {
            var abort = GitCli.Run(worktree, "merge", "--abort");
            if (!abort.Succeeded || abort.DrainTimedOut) return false;
        }
        var branch = "refs/heads/" + GoalWorktrees.BranchName(goal.Id);
        if (Run(worktree, "symbolic-ref", "HEAD").Trim() != branch) return false;
        var reset = GitCli.Run(worktree, "update-ref", branch, round.OriginalHead, candidate);
        if (!reset.Succeeded || reset.DrainTimedOut) return false;
        Run(worktree, "reset", "--hard");
        return ReadRef(worktree, "HEAD") == round.OriginalHead && !GitCli.IsWorktreeDirty(worktree) && ReadRef(worktree, "MERGE_HEAD") is null;
    }

    private static readonly string[] Identity = ["-c", "user.name=mcg-orchestrator", "-c", "user.email=mcg-orchestrator@localhost", "-c", "commit.gpgSign=false"];
    private static bool SamePaths(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private static string? ReadRef(string worktree, string reference)
    {
        var result = GitCli.Run(worktree, "rev-parse", "--verify", "--quiet", reference);
        if (result.Succeeded && !result.DrainTimedOut) return result.Output.Trim();
        if (result.ExitCode == 1 && !result.DrainTimedOut) return null;
        throw new InvalidOperationException("Could not read git reference " + reference + ": " + result.Error);
    }
    private static string Run(string worktree, params string[] args)
    {
        var result = GitCli.Run(worktree, args);
        if (!result.Succeeded || result.DrainTimedOut)
            throw new InvalidOperationException("git " + string.Join(' ', args) + ": " + result.Error);
        return result.Output;
    }
}
