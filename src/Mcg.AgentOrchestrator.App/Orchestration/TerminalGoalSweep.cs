using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record TerminalGoalSweepRepair(
    string Kind,
    string Evidence,
    string Command);

internal sealed record TerminalGoalSweepBlocker(
    string Kind,
    string Evidence,
    string Command);

internal sealed record TerminalGoalSweepGoalResult(
    GoalId GoalId,
    string GoalPrefix,
    IReadOnlyList<TerminalGoalSweepRepair> Repairs,
    IReadOnlyList<TerminalGoalSweepBlocker> Blockers)
{
    public bool Changed => Repairs.Count > 0;
}

internal sealed record TerminalGoalSweepResult(
    IReadOnlyList<TerminalGoalSweepGoalResult> Goals,
    int ExcludedGoalCount = 0,
    int CacheHitCount = 0,
    int CacheMissCount = 0)
{
    public bool Changed => Goals.Any(goal => goal.Changed);
    public IReadOnlyList<TerminalGoalSweepBlocker> Blockers => Goals.SelectMany(goal => goal.Blockers).ToArray();
}

internal sealed class TerminalGoalSweepCache
{
    private const int StoreVersion = 1;
    private const string StoreFileName = "terminal-goal-sweep-cache.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<GoalId, TerminalGoalSweepCacheEntry> _terminalFingerprints = [];
    private string? _loadedStorePath;
    private bool _loaded;

    internal int Count => _terminalFingerprints.Count;

    internal bool TryMarkHit(AgentOrchestratorKernel kernel, string executionDirectory, Goal goal, string evidenceKey)
    {
        EnsureLoaded(executionDirectory);
        if (!CanCache(executionDirectory, goal))
        {
            Remove(goal.Id);
            return false;
        }

        var fingerprint = BuildFingerprint(kernel, goal);
        return _terminalFingerprints.TryGetValue(goal.Id, out var cached) &&
            string.Equals(cached.EvidenceKey, evidenceKey, StringComparison.Ordinal) &&
            string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal);
    }

    internal void Record(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        Goal goal,
        string evidenceKey,
        IReadOnlyList<TerminalGoalSweepBlocker> blockers)
    {
        EnsureLoaded(executionDirectory);
        if (!CanCache(executionDirectory, goal) || blockers.Any(IsCleanupBlocker))
        {
            Remove(goal.Id);
            return;
        }

        _terminalFingerprints[goal.Id] = new TerminalGoalSweepCacheEntry(evidenceKey, BuildFingerprint(kernel, goal));
        Save();
    }

    private void Remove(GoalId goalId)
    {
        if (_terminalFingerprints.Remove(goalId))
        {
            Save();
        }
    }

    private void EnsureLoaded(string executionDirectory)
    {
        var storePath = StorePath(executionDirectory);
        if (_loaded && string.Equals(_loadedStorePath, storePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _terminalFingerprints.Clear();
        _loaded = true;
        _loadedStorePath = storePath;

        try
        {
            if (!File.Exists(storePath))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<TerminalGoalSweepCacheFile>(File.ReadAllText(storePath), JsonOptions);
            if (file?.Version != StoreVersion)
            {
                return;
            }

            foreach (var item in file.Items)
            {
                if (string.IsNullOrWhiteSpace(item.GoalId) ||
                    string.IsNullOrWhiteSpace(item.EvidenceKey) ||
                    string.IsNullOrWhiteSpace(item.Fingerprint))
                {
                    continue;
                }

                _terminalFingerprints[new GoalId(item.GoalId)] =
                    new TerminalGoalSweepCacheEntry(item.EvidenceKey, item.Fingerprint);
            }
        }
        catch
        {
            _terminalFingerprints.Clear();
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_loadedStorePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_loadedStorePath)!);
            var file = new TerminalGoalSweepCacheFile(
                StoreVersion,
                _terminalFingerprints
                    .OrderBy(item => item.Key.Value, StringComparer.Ordinal)
                    .Select(item => new TerminalGoalSweepCacheFileEntry(
                        item.Key.Value,
                        item.Value.EvidenceKey,
                        item.Value.Fingerprint))
                    .ToArray());
            File.WriteAllText(_loadedStorePath, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch
        {
            // Sweep cache persistence is only a memoization layer; git and kernel state remain authoritative.
        }
    }

    private static string StorePath(string executionDirectory) =>
        Path.Combine(OrchestratorWorkspace.ForDirectory(executionDirectory).OrchestratorDirectory, StoreFileName);

    private static bool CanCache(string executionDirectory, Goal goal) =>
        TerminalGoalSweep.IsTerminalSweepStatus(goal.Status) &&
        goal.Tasks.All(task => !TerminalGoalSweep.IsStaleTerminalAssignedTaskStatus(task.Status)) &&
        goal.Tasks.All(task => task.LastProcess is not { IsRunning: true }) &&
        !HasPendingCleanupBackoff(executionDirectory, goal.Id);

    private static bool IsCleanupBlocker(TerminalGoalSweepBlocker blocker) =>
        blocker.Kind is "completed-worktree-cleanup-needed" or
            "owned-ephemeral-cleanup-needed";

    private static bool HasPendingCleanupBackoff(string executionDirectory, GoalId goalId) =>
        EnumerateGoalCleanupPaths(executionDirectory, goalId)
            .Any(path => GoalWorktrees.TryGetCleanupBackoff(path) is not null);

    private static IEnumerable<string> EnumerateGoalCleanupPaths(string executionDirectory, GoalId goalId)
    {
        var root = Path.GetFullPath(executionDirectory);
        var fullGoalId = goalId.Value;
        var prefix = fullGoalId[..Math.Min(8, fullGoalId.Length)];
        yield return GoalWorktrees.WorktreePath(root, goalId);
        yield return Path.Combine(root, ".orchestrator-context", fullGoalId);

        foreach (var rootName in new[] { ".t", ".scratch" })
        {
            var ephemeralRoot = Path.Combine(root, rootName);
            if (!Directory.Exists(ephemeralRoot))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(ephemeralRoot))
            {
                var name = Path.GetFileName(directory);
                if (name.Equals(fullGoalId, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                {
                    yield return directory;
                }
            }
        }
    }

    private static string BuildFingerprint(AgentOrchestratorKernel kernel, Goal goal)
    {
        var visited = new HashSet<GoalId>();
        return BuildGoalFingerprint(kernel, goal, visited);
    }

    private static string BuildGoalFingerprint(
        AgentOrchestratorKernel kernel,
        Goal goal,
        HashSet<GoalId> visited)
    {
        if (!visited.Add(goal.Id))
        {
            return $"{goal.Id.Value}:cycle";
        }

        var taskParts = goal.Tasks
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .Select(task =>
                string.Join(
                    ":",
                    task.Id.Value,
                    task.Status.ToString(),
                    task.LastDispatch is null ? "dispatch=none" : $"dispatch={task.LastDispatch.DispatchedAt.UtcTicks}:{task.LastDispatch.WorkerName}:{task.LastDispatch.ResultCommit}",
                    task.LastProcess is null ? "process=none" : $"process={task.LastProcess.IsRunning}:{task.LastProcess.CompletedAt?.UtcTicks}:{task.LastProcess.ExitCode}:{task.LastProcess.WasCancelled}",
                    task.LastVerification is null ? "verification=none" : $"verification={task.LastVerification.Succeeded}:{task.LastVerification.ExitCode}:{task.LastVerification.CompletedAt.UtcTicks}",
                    task.LastExecution is null ? "execution=none" : $"execution={task.LastExecution.StopReason}:{task.LastExecution.CompletedAt.UtcTicks}"));

        var dependencyParts = goal.DependsOn
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id =>
            {
                var dependency = kernel.Goals.FirstOrDefault(candidate => candidate.Id == id);
                return dependency is null
                    ? $"{id.Value}:missing"
                    : BuildGoalFingerprint(kernel, dependency, visited);
            });

        return string.Join(
            "|",
            new[]
            {
                goal.Id.Value,
                goal.Status.ToString(),
                $"timeline={goal.Timeline.Count}",
                $"deps={string.Join(",", goal.DependsOn.Select(id => id.Value).Order(StringComparer.Ordinal))}"
            }
            .Concat(taskParts)
            .Concat(dependencyParts));
    }

    private sealed record TerminalGoalSweepCacheEntry(string EvidenceKey, string Fingerprint);

    private sealed record TerminalGoalSweepCacheFile(int Version, IReadOnlyList<TerminalGoalSweepCacheFileEntry> Items);

    private sealed record TerminalGoalSweepCacheFileEntry(string GoalId, string EvidenceKey, string Fingerprint);
}

internal static class TerminalGoalSweep
{
    internal static Func<string, IReadOnlyList<string>, GitCli.GitResult> GitRunner { get; set; } =
        (workingDirectory, args) => GitCli.Run(workingDirectory, args.ToArray());

    public static TerminalGoalSweepResult Run(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null,
        TerminalGoalSweepCache? cache = null)
    {
        var dispatchRunner = new BackgroundDispatchRunner();
        GoalBranchFactIndex? branchFactIndex = null;
        var results = new List<TerminalGoalSweepGoalResult>();
        var cacheHits = 0;
        var cacheMisses = 0;

        foreach (var originalGoal in kernel.Goals.Where(goal => onlyGoalId is null || goal.Id == onlyGoalId).ToArray())
        {
            var cacheEvidenceKey = string.Empty;
            if (cache is not null && IsTerminalSweepStatus(originalGoal.Status))
            {
                branchFactIndex ??= GoalBranchFactIndex.Build(executionDirectory);
                cacheEvidenceKey = branchFactIndex.BuildGoalEvidenceKey(originalGoal);
            }

            if (cache is not null &&
                cacheEvidenceKey.Length > 0 &&
                cache.TryMarkHit(kernel, executionDirectory, originalGoal, cacheEvidenceKey))
            {
                cacheHits++;
                continue;
            }

            if (cache is not null && IsTerminalSweepStatus(originalGoal.Status))
            {
                cacheMisses++;
            }

            var repairs = new List<TerminalGoalSweepRepair>();
            var blockers = new List<TerminalGoalSweepBlocker>();
            var prefix = originalGoal.Id.Value[..Math.Min(8, originalGoal.Id.Value.Length)];

            var reconciled = dispatchRunner.SweepExitedProcesses(kernel, originalGoal.Id);
            if (reconciled > 0)
            {
                repairs.Add(new TerminalGoalSweepRepair(
                    "dispatch-exit-reconciled",
                    $"reconciled {reconciled} exited dispatch artifact(s)",
                    "reconcile"));
            }

            var goal = kernel.GetGoal(originalGoal.Id);
            branchFactIndex ??= GoalBranchFactIndex.Build(executionDirectory);
            var branchFacts = branchFactIndex.BuildGoalBranchFacts(goal);
            var hasTerminalTaskDesync = TryBuildTerminalTaskDesyncEvidence(goal, out var desyncEvidence);
            var blockedByDirtyWorktree = false;

            if (TryReconcileVerifiedMissingBranchOrWorktree(
                    kernel,
                    executionDirectory,
                    goal,
                    branchFacts,
                    prefix,
                    repairs))
            {
                goal = kernel.GetGoal(originalGoal.Id);
                branchFacts = branchFactIndex.BuildGoalBranchFacts(goal);
            }

            if (hasTerminalTaskDesync &&
                TryBuildTerminalDirtyWorktreeBlocker(goal, executionDirectory, prefix, desyncEvidence, out var dirtyEvidence, out var dirtyCommand))
            {
                blockedByDirtyWorktree = true;
                blockers.Add(new TerminalGoalSweepBlocker(
                    "terminal-dirty-worktree",
                    dirtyEvidence,
                    dirtyCommand));
            }
            else if (branchFacts.BranchAlreadyLanded)
            {
                foreach (var task in goal.Tasks.Where(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)).ToArray())
                {
                    var staleStatus = task.Status;
                    kernel.ReportTaskProgress(
                        goal.Id,
                        task.Id,
                        WorkTaskStatus.Cancelled,
                        "Terminal stale-goal sweep cancelled stale task because the goal branch is already landed.");
                    repairs.Add(new TerminalGoalSweepRepair(
                        "landed-task-desync",
                        $"landed goal had stale {staleStatus} task {task.Id.Value[..8]}",
                        $"workspace remove {prefix}"));
                }

                goal = kernel.GetGoal(originalGoal.Id);
                branchFacts = branchFactIndex.BuildGoalBranchFacts(goal);
            }

            if (!blockedByDirtyWorktree &&
                !branchFacts.BranchAlreadyLanded &&
                TryBuildTerminalLiveDispatchBlocker(goal, prefix, out var liveDispatchEvidence, out var liveDispatchCommand))
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "terminal-live-dispatch",
                    liveDispatchEvidence,
                    liveDispatchCommand));
            }
            else if (!blockedByDirtyWorktree &&
                     !branchFacts.BranchAlreadyLanded &&
                     onlyGoalId is null &&
                     TryBuildGlobalStaleTerminalExclusionEvidence(goal, out var staleTerminalExclusionEvidence))
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "stale-terminal-excluded",
                    staleTerminalExclusionEvidence,
                    "excluded"));
            }
            else if (!blockedByDirtyWorktree &&
                     !branchFacts.BranchAlreadyLanded &&
                     hasTerminalTaskDesync &&
                     kernel.NormalizeGoalLifecycleState(goal.Id, "terminal stale-goal sweep: reopened terminal goal with non-terminal task(s)."))
            {
                repairs.Add(new TerminalGoalSweepRepair(
                    "terminal-task-desync",
                    desyncEvidence,
                    $"conduct {prefix} --loop"));
                goal = kernel.GetGoal(originalGoal.Id);
                branchFacts = branchFactIndex.BuildGoalBranchFacts(goal);
            }

            if (!blockedByDirtyWorktree &&
                branchFacts.IsAcceptedOrVerifiedGitGoal &&
                branchFacts.HasGoalBranchArtifact &&
                !branchFacts.BranchAlreadyLanded)
            {
                if (goal.Status == GoalStatus.Completed &&
                    kernel.NormalizePrematureCompletedGoalToVerified(
                        goal.Id,
                        "terminal stale-goal sweep: normalized raw Completed goal with unmerged branch back to Verified for acceptance."))
                {
                    repairs.Add(new TerminalGoalSweepRepair(
                        "completed-branch-normalized",
                        $"completed goal with unmerged branch {GoalWorktrees.BranchName(goal.Id)} was normalized to Verified",
                        $"acceptance {prefix}"));
                    goal = kernel.GetGoal(originalGoal.Id);
                    branchFacts = branchFactIndex.BuildGoalBranchFacts(goal);
                }

                blockers.Add(new TerminalGoalSweepBlocker(
                    "completed-branch-unmerged",
                    $"{goal.Status.ToString().ToLowerInvariant()} goal still has unmerged branch {GoalWorktrees.BranchName(goal.Id)}",
                    $"acceptance {prefix}"));
                results.Add(new TerminalGoalSweepGoalResult(originalGoal.Id, prefix, repairs, blockers));
                continue;
            }

            if (!blockedByDirtyWorktree &&
                (branchFacts.IsCompletedGitGoal || (goal.Status == GoalStatus.Verified && branchFacts.BranchAlreadyLanded)))
            {
                var removeResult = GoalWorktrees.Remove(executionDirectory, goal.Id, kernel);
                if (removeResult.Message.Contains("kept because it has unmerged commits", StringComparison.OrdinalIgnoreCase))
                {
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "completed-branch-unmerged",
                        removeResult.Message,
                        $"acceptance {prefix}"));
                }
                else if (!removeResult.IsComplete)
                {
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "completed-worktree-cleanup-needed",
                        removeResult.Message,
                        removeResult.ResumeCommand ?? $"conduct {prefix} --loop"));
                }
                else if (!removeResult.Message.Contains("already clean", StringComparison.OrdinalIgnoreCase))
                {
                    RecordTerminalDisposition(
                        kernel,
                        executionDirectory,
                        goal,
                        GoalTerminalDispositionKind.Landed,
                        $"Terminal sweep completed merged goal cleanup: {removeResult.Message}");
                    repairs.Add(new TerminalGoalSweepRepair(
                        "merged-branch-cleanup",
                        removeResult.Message,
                        $"workspace remove {prefix}"));
                    goal = kernel.GetGoal(originalGoal.Id);
                }

                AddOwnedEphemeralCleanupRepair(removeResult.OwnedEphemeralCleanup, prefix, repairs);
            }

            if (IsTerminalSweepStatus(goal.Status) && !blockers.Any(IsTerminalCleanupBlockingBlocker))
            {
                var ephemeralCleanup = GoalWorktrees.SweepOwnedEphemeralDirectories(executionDirectory, goal.Id, kernel);
                if (!ephemeralCleanup.IsComplete)
                {
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "owned-ephemeral-cleanup-needed",
                        $"owned ephemeral cleanup incomplete; leftovers={string.Join(",", ephemeralCleanup.LeftoverPaths)}",
                        $"conduct {prefix} --loop"));
                }
                else if (ephemeralCleanup.RemovedCount > 0)
                {
                    repairs.Add(new TerminalGoalSweepRepair(
                        "owned-ephemeral-cleanup",
                        $"removed {ephemeralCleanup.RemovedCount} owned ephemeral director{(ephemeralCleanup.RemovedCount == 1 ? "y" : "ies")}",
                        $"conduct {prefix} --loop"));
                }
            }

            if (repairs.Count > 0 || blockers.Count > 0)
            {
                results.Add(new TerminalGoalSweepGoalResult(originalGoal.Id, prefix, repairs, blockers));
            }

            if (cache is not null)
            {
                var currentGoal = kernel.GetGoal(originalGoal.Id);
                var currentEvidenceKey = IsTerminalSweepStatus(currentGoal.Status)
                    ? branchFactIndex!.BuildGoalEvidenceKey(currentGoal)
                    : cacheEvidenceKey;
                cache.Record(kernel, executionDirectory, currentGoal, currentEvidenceKey, blockers);
            }
        }

        return new TerminalGoalSweepResult(
            results,
            CountGlobalStaleTerminalExclusions(results),
            cacheHits,
            cacheMisses);
    }

    private sealed record GoalBranchFacts(
        bool IsAcceptedOrVerifiedGitGoal,
        bool IsCompletedGitGoal,
        bool HasRegisteredWorktree,
        bool HasGoalBranch,
        bool HasGoalBranchArtifact,
        bool BranchAlreadyLanded)
    {
        public bool MissingBranchOrWorktree => IsAcceptedOrVerifiedGitGoal && (!HasRegisteredWorktree || !HasGoalBranch);
    }

    private sealed class GoalBranchFactIndex(
        string executionDirectory,
        bool isGitWorkTree,
        IReadOnlyDictionary<string, string> goalBranchTips,
        IReadOnlySet<string> mergedGoalBranches,
        IReadOnlySet<string> registeredWorktreePaths)
    {
        public static GoalBranchFactIndex Build(string executionDirectory)
        {
            var fullExecutionDirectory = Path.GetFullPath(executionDirectory);
            var branchResult = RunGit(fullExecutionDirectory, "for-each-ref", "--format=%(refname:short) %(objectname)", "refs/heads/goal/");
            if (branchResult.ExitCode != 0)
            {
                return new GoalBranchFactIndex(fullExecutionDirectory, false, EmptyTipMap(), EmptySet(), EmptyPathSet());
            }

            var mergedResult = RunGit(fullExecutionDirectory, "for-each-ref", "--format=%(refname:short)", "--merged", "HEAD", "refs/heads/goal/");
            var worktreeResult = RunGit(fullExecutionDirectory, "worktree", "list", "--porcelain");

            return new GoalBranchFactIndex(
                fullExecutionDirectory,
                true,
                ParseBranchTips(branchResult.Output),
                mergedResult.ExitCode == 0 ? ParseLines(mergedResult.Output) : EmptySet(),
                worktreeResult.ExitCode == 0 ? ParseWorktreePaths(worktreeResult.Output) : EmptyPathSet());
        }

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

        private static IReadOnlySet<string> ParseLines(string output) =>
            output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

        private static IReadOnlyDictionary<string, string> ParseBranchTips(string output)
        {
            var tips = EmptyTipMap();
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                {
                    tips[parts[0]] = parts[1];
                }
            }

            return tips;
        }

        private static HashSet<string> ParseWorktreePaths(string output)
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

        private static HashSet<string> EmptySet() =>
            new(StringComparer.Ordinal);

        private static Dictionary<string, string> EmptyTipMap() =>
            new(StringComparer.Ordinal);

        private static HashSet<string> EmptyPathSet() =>
            new(StringComparer.OrdinalIgnoreCase);

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryReconcileVerifiedMissingBranchOrWorktree(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        Goal goal,
        GoalBranchFacts branchFacts,
        string prefix,
        List<TerminalGoalSweepRepair> repairs)
    {
        if (goal.Status != GoalStatus.Verified ||
            !branchFacts.IsAcceptedOrVerifiedGitGoal ||
            branchFacts.BranchAlreadyLanded ||
            !branchFacts.MissingBranchOrWorktree)
        {
            return false;
        }

        var missing = !branchFacts.HasGoalBranch
            ? $"branch {GoalWorktrees.BranchName(goal.Id)} is missing"
            : "registered worktree is missing";

        if (TryBuildReachableCommitEvidence(executionDirectory, goal, out var landedEvidence))
        {
            RecordTerminalDisposition(
                kernel,
                executionDirectory,
                goal,
                GoalTerminalDispositionKind.Landed,
                $"Terminal sweep reconciled missing goal artifact as landed: {landedEvidence}.");
            repairs.Add(new TerminalGoalSweepRepair(
                "missing-branch-landed-reconciled",
                $"{missing}; {landedEvidence}",
                $"conduct {prefix} --loop"));
            return true;
        }

        RecordTerminalDisposition(
            kernel,
            executionDirectory,
            goal,
            GoalTerminalDispositionKind.Retired,
            $"Terminal sweep retired missing goal artifact because landing could not be verified from recorded commits, integration commits, or dogfood log: {missing}.");
        repairs.Add(new TerminalGoalSweepRepair(
            "missing-branch-retired",
            $"{missing}; landing not verifiable from recorded commits, integration commits, or dogfood log; record retired from future conduct sweeps",
            "retired"));
        return true;
    }

    private static bool TryBuildReachableCommitEvidence(
        string executionDirectory,
        Goal goal,
        out string evidence)
    {
        var commits = goal.Tasks
            .Select(task => task.LastDispatch?.ResultCommit)
            .Where(commit => !string.IsNullOrWhiteSpace(commit))
            .Select(commit => commit!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (commits.Length > 0 &&
            commits.All(commit => IsCommitReachableFromHead(executionDirectory, commit)))
        {
            evidence = $"reachableResultCommits={string.Join(",", commits)}";
            return true;
        }

        if (TryBuildReachableIntegrationCommitEvidence(executionDirectory, goal, out evidence))
        {
            return true;
        }

        if (TryBuildDogfoodLogEvidence(executionDirectory, goal, out evidence))
        {
            return true;
        }

        evidence = string.Empty;
        return false;
    }

    private static bool IsCommitReachableFromHead(string executionDirectory, string commit) =>
        GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", commit, "HEAD").ExitCode == 0;

    private static bool TryBuildReachableIntegrationCommitEvidence(
        string executionDirectory,
        Goal goal,
        out string evidence)
    {
        var goalPrefix = goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)];
        foreach (var pattern in new[] { goal.Id.Value, goalPrefix, GoalWorktrees.BranchName(goal.Id) })
        {
            var result = GitCli.Run(
                executionDirectory,
                "log",
                "--format=%H",
                "-n",
                "1",
                "--regexp-ignore-case",
                $"--grep={pattern}",
                "HEAD");
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
            {
                var commit = result.Output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(commit))
                {
                    evidence = $"reachableIntegrationCommit={commit}; matched={pattern}";
                    return true;
                }
            }
        }

        evidence = string.Empty;
        return false;
    }

    private static bool TryBuildDogfoodLogEvidence(
        string executionDirectory,
        Goal goal,
        out string evidence)
    {
        var dogfoodLogPath = Path.Combine(executionDirectory, ".orchestrator", "dogfood-log.db");
        if (!File.Exists(dogfoodLogPath))
        {
            evidence = string.Empty;
            return false;
        }

        try
        {
            var record = new DogfoodLogStore(dogfoodLogPath)
                .GetByGoalIdAsync(goal.Id.Value)
                .GetAwaiter()
                .GetResult();
            if (record is not null)
            {
                evidence = $"dogfoodLogSequence={record.Sequence}; recordedAt={record.RecordedAt:O}";
                return true;
            }
        }
        catch
        {
            // Sweep repair remains best-effort; unreadable evidence should not block the fallback path.
        }

        evidence = string.Empty;
        return false;
    }

    private static void RecordTerminalDisposition(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        Goal goal,
        GoalTerminalDispositionKind kind,
        string detail)
    {
        GoalOperationJournal.RecordTerminalDisposition(
            executionDirectory,
            goal,
            new GoalTerminalDisposition(kind, detail));
        kernel.CompleteGoal(goal.Id, detail);
    }

    public static TerminalGoalSweepResult Diagnose(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null)
    {
        var results = new List<TerminalGoalSweepGoalResult>();
        var branchFactIndex = GoalBranchFactIndex.Build(executionDirectory);

        foreach (var goal in kernel.Goals.Where(goal => onlyGoalId is null || goal.Id == onlyGoalId).ToArray())
        {
            var blockers = new List<TerminalGoalSweepBlocker>();
            var prefix = goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)];

            if (TryBuildTerminalLiveDispatchBlocker(goal, prefix, out var liveDispatchEvidence, out var liveDispatchCommand))
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "terminal-live-dispatch",
                    liveDispatchEvidence,
                    liveDispatchCommand));
            }

            var branchFacts = branchFactIndex.BuildGoalBranchFacts(goal);
            if (branchFacts.IsAcceptedOrVerifiedGitGoal &&
                branchFacts.HasGoalBranchArtifact &&
                !branchFacts.BranchAlreadyLanded)
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "completed-branch-unmerged",
                    $"{goal.Status.ToString().ToLowerInvariant()} goal still has unmerged branch {GoalWorktrees.BranchName(goal.Id)}",
                    $"acceptance {prefix}"));
            }

            if (blockers.Count > 0)
            {
                results.Add(new TerminalGoalSweepGoalResult(goal.Id, prefix, [], blockers));
            }
        }

        return new TerminalGoalSweepResult(results, CountGlobalStaleTerminalExclusions(results));
    }

    private static void AddOwnedEphemeralCleanupRepair(
        GoalOwnedEphemeralSweepResult? ephemeralCleanup,
        string prefix,
        List<TerminalGoalSweepRepair> repairs)
    {
        if (ephemeralCleanup is not { RemovedCount: > 0 })
        {
            return;
        }

        repairs.Add(new TerminalGoalSweepRepair(
            "owned-ephemeral-cleanup",
            $"removed {ephemeralCleanup.RemovedCount} owned ephemeral director{(ephemeralCleanup.RemovedCount == 1 ? "y" : "ies")}",
            $"conduct {prefix} --loop"));
    }

    private static bool TryBuildGlobalStaleTerminalExclusionEvidence(Goal goal, out string evidence)
    {
        evidence = string.Empty;
        if (!IsGlobalStaleTerminalStatus(goal.Status))
        {
            return false;
        }

        var dispatchableTasks = goal.Tasks
            .Where(task => IsStaleTerminalAssignedTaskStatus(task.Status))
            .Select(task => $"{task.Id.Value[..8]}:{task.Status}")
            .ToArray();
        if (dispatchableTasks.Length == 0)
        {
            return false;
        }

        evidence = $"goalId={goal.Id.Value}; goalState={goal.Status}; action=excluded; dispatchableTasks={string.Join(",", dispatchableTasks)}";
        return true;
    }

    private static bool TryBuildTerminalTaskDesyncEvidence(Goal goal, out string evidence)
    {
        evidence = string.Empty;
        if (!IsTerminalSweepStatus(goal.Status))
        {
            return false;
        }

        var dispatchableTasks = goal.Tasks
            .Where(task => IsStaleTerminalAssignedTaskStatus(task.Status))
            .Select(task => $"{task.Id.Value[..8]}:{task.Status}")
            .ToArray();
        if (dispatchableTasks.Length == 0)
        {
            return false;
        }

        evidence = $"goalState={goal.Status}; dispatchableTasks={string.Join(",", dispatchableTasks)}";
        return true;
    }

    private static bool TryBuildTerminalDirtyWorktreeBlocker(
        Goal goal,
        string executionDirectory,
        string goalPrefix,
        string desyncEvidence,
        out string evidence,
        out string command)
    {
        evidence = string.Empty;
        command = string.Empty;
        if (!GoalWorktrees.IsGitWorkTree(executionDirectory))
        {
            return false;
        }

        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        if (worktree is null || GoalWorktrees.IsWorktreeClean(executionDirectory, goal.Id))
        {
            return false;
        }

        evidence = $"{desyncEvidence}; worktreeDirty=true; worktree={worktree}";
        command = $"goal-recovery {goalPrefix}";
        return true;
    }

    private static bool TryBuildTerminalLiveDispatchBlocker(
        Goal goal,
        string goalPrefix,
        out string evidence,
        out string command)
    {
        evidence = string.Empty;
        command = string.Empty;
        if (!IsTerminalSweepStatus(goal.Status))
        {
            return false;
        }

        var liveTasks = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Running &&
                           task.LastProcess is { IsRunning: true } process &&
                           process.TrackedProcessIds.Any(IsProcessAlive))
            .Select(task =>
            {
                var process = task.LastProcess!;
                var livePids = process.TrackedProcessIds.Where(IsProcessAlive).ToArray();
                return new
                {
                    Task = task,
                    TaskNumber = TaskDisplayNumber.Resolve(goal, task.Id),
                    Process = process,
                    LivePids = livePids
                };
            })
            .ToArray();
        if (liveTasks.Length == 0)
        {
            return false;
        }

        evidence =
            $"goalState={goal.Status}; liveDispatchTasks={string.Join(",", liveTasks.Select(item => $"{item.Task.Id.Value[..8]}:{item.Task.Status}:pid={item.Process.ProcessId}:livePids={string.Join("+", item.LivePids)}"))}";
        command = liveTasks.Length == 1
            ? $"refresh-dispatch {goalPrefix} {liveTasks[0].TaskNumber}"
            : $"refresh-dispatches {goalPrefix}";
        return true;
    }

    internal static bool IsTerminalSweepStatus(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Failed or GoalStatus.Superseded;

    private static bool IsGlobalStaleTerminalStatus(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Failed;

    internal static bool IsStaleTerminalAssignedTaskStatus(WorkTaskStatus status) =>
        status is WorkTaskStatus.Assigned or WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman;

    private static bool IsTerminalCleanupBlockingBlocker(TerminalGoalSweepBlocker blocker) =>
        blocker.Kind is "terminal-dirty-worktree" or
            "terminal-live-dispatch" or
            "stale-terminal-excluded" or
            "completed-branch-unmerged";

    private static int CountGlobalStaleTerminalExclusions(IEnumerable<TerminalGoalSweepGoalResult> results) =>
        results.Sum(goal => goal.Blockers.Count(blocker => blocker.Kind == "stale-terminal-excluded"));

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
