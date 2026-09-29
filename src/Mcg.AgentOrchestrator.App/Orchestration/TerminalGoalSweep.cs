using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record TerminalGoalSweepRepair(
    string Kind,
    string Evidence,
    string Command);

internal sealed record TerminalGoalSweepBlocker(
    string Kind,
    string Evidence,
    TerminalGoalRemedy Remedy)
{
    public TerminalGoalSweepBlocker(string kind, string evidence, string command)
        : this(kind, evidence, TerminalGoalRemedy.OperatorOnly(new GoalId("unknown"), "unknown", command))
    {
    }

    public TerminalGoalSweepBlocker(string kind, string evidence, string command, GoalId goalId, string goalPrefix)
        : this(kind, evidence, TerminalGoalRemedy.OperatorOnly(goalId, goalPrefix, command))
    {
    }

    public string Command => Remedy.RenderCommand();
}

internal sealed record TerminalGoalSweepReconciliationReceipt(
    string IntegrateSha,
    string MainSha,
    GoalStatus PriorStatus,
    string? RegisteredWorktreePath,
    bool CleanupPreviouslyRecorded,
    IReadOnlyList<GoalTerminalReconciliationEvidence> AcceptanceAttempts,
    bool RecoveredFromJournalReceipt);

internal sealed record TerminalGoalSweepGoalResult(
    GoalId GoalId,
    string GoalPrefix,
    IReadOnlyList<TerminalGoalSweepRepair> Repairs,
    IReadOnlyList<TerminalGoalSweepBlocker> Blockers,
    TerminalGoalSweepReconciliationReceipt? Reconciliation = null)
{
    public bool Changed => Repairs.Count > 0;
}

internal sealed record TerminalGoalSweepResult(
    IReadOnlyList<TerminalGoalSweepGoalResult> Goals,
    int ExcludedGoalCount = 0,
    int CacheHitCount = 0,
    int CacheMissCount = 0,
    IReadOnlyList<GoalId>? SweptGoalIds = null,
    int TerminalizedGoalCount = 0,
    int ResolvedAttentionItemCount = 0,
    IReadOnlyList<string>? ProgressEvents = null,
    IReadOnlyList<GoalId>? TerminalizedGoalIds = null)
{
    public long GitIndexDurationMs { get; init; }
    public long EvidenceDurationMs { get; init; }
    public long EphemeralDurationMs { get; init; }
    public long AttentionDurationMs { get; init; }
    public long MergeEvidenceDurationMs { get; init; }
    public long GoalsDurationMs { get; init; }
    public long OwnedRootDurationMs { get; init; }
    public int GitSpawnCount { get; init; }
    public int GoalsSweptCount { get; init; }
    public TerminalGoalSweepOwnedRootResult? OwnedRoots { get; init; }
    public bool Changed => Goals.Any(goal => goal.Changed);
    public IReadOnlyList<TerminalGoalSweepBlocker> Blockers => Goals.SelectMany(goal => goal.Blockers).ToArray();
    public IReadOnlyList<GoalId> ExplicitlySweptGoalIds =>
        SweptGoalIds ?? Goals.Select(goal => goal.GoalId).Distinct().ToArray();
    public IReadOnlyList<string> Events => ProgressEvents ?? [];
    public IReadOnlyList<GoalId> ExplicitlyTerminalizedGoalIds => TerminalizedGoalIds ?? [];

    public TerminalGoalSweepResult PreserveTerminalizationsFrom(TerminalGoalSweepResult prior)
    {
        ArgumentNullException.ThrowIfNull(prior);
        return this with
        {
            TerminalizedGoalCount = prior.TerminalizedGoalCount + TerminalizedGoalCount,
            TerminalizedGoalIds = prior.ExplicitlyTerminalizedGoalIds
                .Concat(ExplicitlyTerminalizedGoalIds)
                .Distinct()
                .ToArray()
        };
    }
}

internal sealed class TerminalGoalSweepCache
{
    private const int StoreVersion = 1;
    private const string StoreFileName = "terminal-goal-sweep-cache.json";
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly Dictionary<GoalId, TerminalGoalSweepCacheEntry> _terminalFingerprints = [];
    private string? _integrationEvidenceDirectory;
    private string? _integrationEvidenceMainSha;
    private Func<string, IReadOnlyList<string>, GitCli.GitResult>? _integrationEvidenceGitRunner;
    private IGoalIntegrationEvidenceResolver? _integrationEvidenceResolver;
    private string? _loadedStorePath;
    private bool _loaded;
    private bool _dirty;

    internal int Count => _terminalFingerprints.Count;

    internal IGoalIntegrationEvidenceResolver GetIntegrationEvidenceResolver(
        string executionDirectory,
        string? mainSha,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunnerIdentity,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        var fullDirectory = Path.GetFullPath(executionDirectory);
        if (_integrationEvidenceResolver is not null &&
            string.Equals(_integrationEvidenceDirectory, fullDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_integrationEvidenceMainSha, mainSha, StringComparison.Ordinal) &&
            ReferenceEquals(_integrationEvidenceGitRunner, gitRunnerIdentity))
        {
            return _integrationEvidenceResolver;
        }

        _integrationEvidenceDirectory = fullDirectory;
        _integrationEvidenceMainSha = mainSha;
        _integrationEvidenceGitRunner = gitRunnerIdentity;
        _integrationEvidenceResolver = GoalIntegrationEvidenceResolver.Build(fullDirectory, mainSha, gitRunner);
        return _integrationEvidenceResolver;
    }

    internal bool TryMarkHit(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        Goal goal,
        string evidenceKey,
        TerminalGoalSweepCacheSweepFacts sweepFacts)
    {
        EnsureLoaded(executionDirectory);
        if (!CanCache(executionDirectory, goal, sweepFacts))
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
        IReadOnlyList<TerminalGoalSweepBlocker> blockers,
        TerminalGoalSweepCacheSweepFacts sweepFacts)
    {
        EnsureLoaded(executionDirectory);
        if (!CanCache(executionDirectory, goal, sweepFacts) || blockers.Any(IsCleanupBlocker))
        {
            Remove(goal.Id);
            return;
        }

        var entry = new TerminalGoalSweepCacheEntry(evidenceKey, BuildFingerprint(kernel, goal));
        if (!_terminalFingerprints.TryGetValue(goal.Id, out var existing) || existing != entry)
        {
            _terminalFingerprints[goal.Id] = entry;
            _dirty = true;
        }
    }

    private void Remove(GoalId goalId)
    {
        if (_terminalFingerprints.Remove(goalId))
        {
            _dirty = true;
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
        _dirty = false;
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

    internal void Flush()
    {
        if (!_dirty || string.IsNullOrWhiteSpace(_loadedStorePath))
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
            _dirty = false;
        }
        catch
        {
            // This cache is derivative of git and kernel state. A failed or crash-lost flush only causes
            // the next sweep to re-probe; it cannot make a stale cache entry authoritative.
        }
    }

    private static string StorePath(string executionDirectory) =>
        Path.Combine(OrchestratorWorkspace.ForDirectory(executionDirectory).OrchestratorDirectory, StoreFileName);

    private static bool CanCache(
        string executionDirectory,
        Goal goal,
        TerminalGoalSweepCacheSweepFacts sweepFacts) =>
        TerminalGoalSweep.IsTerminalSweepStatus(goal.Status) &&
        goal.Tasks.All(task => !TerminalGoalSweep.IsStaleTerminalAssignedTaskStatus(task.Status)) &&
        goal.Tasks.All(task => task.LastProcess is not { IsRunning: true }) &&
        !HasPendingGoalArtifactCleanup(executionDirectory, goal.Id, sweepFacts.GitFacts) &&
        !HasPendingCleanupBackoff(executionDirectory, goal.Id, sweepFacts.EphemeralDirectories, sweepFacts.CleanupHooks);

    private static bool IsCleanupBlocker(TerminalGoalSweepBlocker blocker) =>
        blocker.Kind is "completed-worktree-cleanup-needed" or
            "owned-ephemeral-cleanup-needed";

    private static bool HasPendingCleanupBackoff(
        string executionDirectory,
        GoalId goalId,
        IReadOnlyList<string> ephemeralDirectories,
        GoalWorktreeCleanupHooks cleanupHooks) =>
        EnumerateGoalCleanupPaths(executionDirectory, goalId, ephemeralDirectories)
            .Any(path => GoalWorktrees.TryGetCleanupBackoff(path, cleanupHooks) is not null);

    private static bool HasPendingGoalArtifactCleanup(
        string executionDirectory,
        GoalId goalId,
        GoalGitFactIndex gitFacts) =>
        GoalWorktrees.TryResolve(executionDirectory, goalId) is not null ||
        gitFacts.HasGoalBranch(goalId);

    private static IEnumerable<string> EnumerateGoalCleanupPaths(
        string executionDirectory,
        GoalId goalId,
        IReadOnlyList<string> ephemeralDirectories)
    {
        var root = Path.GetFullPath(executionDirectory);
        var fullGoalId = goalId.Value;
        var prefix = fullGoalId[..Math.Min(8, fullGoalId.Length)];
        yield return GoalWorktrees.WorktreePath(root, goalId);
        yield return Path.Combine(root, ".orchestrator-context", fullGoalId);

        foreach (var directory in ephemeralDirectories)
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

internal sealed record TerminalGoalSweepCacheSweepFacts(
    GoalGitFactIndex GitFacts,
    IReadOnlyList<string> EphemeralDirectories,
    GoalWorktreeCleanupHooks CleanupHooks);

internal static partial class TerminalGoalSweep
{
    private static readonly AsyncLocal<GitSpawnCounter?> CurrentGitSpawnCounter = new();

    private sealed class GitSpawnCounter
    {
        public int Value;
    }

    private sealed class GitSpawnCounterScope(GitSpawnCounter counter) : IDisposable
    {
        private readonly GitSpawnCounter? _prior = CurrentGitSpawnCounter.Value;

        public void Enter() => CurrentGitSpawnCounter.Value = counter;
        public void Dispose() => CurrentGitSpawnCounter.Value = _prior;
    }

    internal const int MaxMergeEvidenceTerminalizationsPerSweep = 25;

    internal static Func<string, IReadOnlyList<string>, GitCli.GitResult> GitRunner
    {
        get => GoalGitFactIndex.GitRunner;
        set
        {
            GoalGitFactIndex.GitRunner = value;
            GoalIntegrationEvidenceResolver.GitRunner = value;
        }
    }

    public static TerminalGoalSweepResult Run(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null,
        TerminalGoalSweepCache? cache = null,
        IGoalIntegrationEvidenceResolver? integrationEvidenceResolver = null,
        ICollaborationItemStore? attentionStore = null,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null,
        GoalWorktreeCleanupHooks? cleanupHooks = null,
        string? orchestratorDirectory = null,
        MergeTrainAcceptanceStore? mergeTrainAcceptanceStore = null,
        CohortAcceptanceStore? cohortAcceptanceStore = null,
        bool reclaimGoalRoots = false)
    {
        gitRunner ??= GitRunner;
        var gitRunnerIdentity = gitRunner;
        var gitSpawnCounter = new GitSpawnCounter();
        using var gitSpawnCounterScope = new GitSpawnCounterScope(gitSpawnCounter);
        gitSpawnCounterScope.Enter();
        GitCli.GitResult CountingGitRunner(string workingDirectory, IReadOnlyList<string> arguments)
        {
            var activeCounter = CurrentGitSpawnCounter.Value
                ?? throw new InvalidOperationException("Sweep git runner invoked outside an active sweep.");
            Interlocked.Increment(ref activeCounter.Value);
            return gitRunnerIdentity(workingDirectory, arguments);
        }
        gitRunner = CountingGitRunner;
        cleanupHooks ??= new GoalWorktreeCleanupHooks();
        var workspace = OrchestratorWorkspace.ForDirectory(executionDirectory);
        orchestratorDirectory ??= workspace.OrchestratorDirectory;
        mergeTrainAcceptanceStore ??= new MergeTrainAcceptanceStore(
            Path.Combine(orchestratorDirectory, "merge-train-acceptance.db"));
        cohortAcceptanceStore ??= new CohortAcceptanceStore(
            Path.Combine(orchestratorDirectory, "cohort-acceptance.db"));
        var dispatchRunner = new BackgroundDispatchRunner
        {
            OperatorIntents = SqliteOperatorIntentStore.ForDirectories(orchestratorDirectory, workspace.LogDirectory)
        };
        var ownedRootTiming = System.Diagnostics.Stopwatch.StartNew();
        var ownedRoots = ReapOwnedBuildRoots(workspace.SqliteStatePath, reclaimGoalRoots);
        ownedRootTiming.Stop();
        var gitIndexTiming = System.Diagnostics.Stopwatch.StartNew();
        var branchFactIndex = GoalGitFactIndex.Build(executionDirectory, gitRunner);
        gitIndexTiming.Stop();
        var evidenceTiming = System.Diagnostics.Stopwatch.StartNew();
        integrationEvidenceResolver ??= cache?.GetIntegrationEvidenceResolver(executionDirectory, branchFactIndex.MainSha, gitRunnerIdentity, gitRunner)
            ?? GoalIntegrationEvidenceResolver.Build(executionDirectory, branchFactIndex.MainSha, gitRunner);
        evidenceTiming.Stop();
        attentionStore ??= CollaborationItemStore.ForDirectory(
            orchestratorDirectory);
        var ephemeralTiming = System.Diagnostics.Stopwatch.StartNew();
        var ephemeralDirectories = EnumerateEphemeralDirectories(executionDirectory);
        ephemeralTiming.Stop();
        var cacheSweepFacts = new TerminalGoalSweepCacheSweepFacts(
            branchFactIndex,
            ephemeralDirectories,
            cleanupHooks);
        var results = new List<TerminalGoalSweepGoalResult>();
        var sweptGoalIds = new List<GoalId>();
        var terminalizedGoalIds = new List<GoalId>();
        var cacheHits = 0;
        var cacheMisses = 0;
        var terminalizedGoalCount = 0;
        var attentionTiming = System.Diagnostics.Stopwatch.StartNew();
        var resolvedAttentionItemCount = ResolveAttentionForExistingTerminalGoals(
            kernel,
            onlyGoalId,
            attentionStore);
        attentionTiming.Stop();
        var mergeEvidenceTiming = System.Diagnostics.Stopwatch.StartNew();
        var integrationEvidenceByGoal = ResolveMergeEvidenceCandidates(
            kernel,
            onlyGoalId,
            integrationEvidenceResolver);
        mergeEvidenceTiming.Stop();
        GoalId? mergeEvidenceBoundGoalId = null;
        var mergeEvidenceCandidateCount = integrationEvidenceByGoal.Count;
        if (integrationEvidenceByGoal.Count > MaxMergeEvidenceTerminalizationsPerSweep)
        {
            mergeEvidenceBoundGoalId = integrationEvidenceByGoal.Keys
                .OrderBy(goalId => goalId.Value, StringComparer.Ordinal)
                .First();
            integrationEvidenceByGoal.Clear();
        }

        var goalsTiming = System.Diagnostics.Stopwatch.StartNew();
        foreach (var originalGoal in kernel.Goals.Where(goal => onlyGoalId is null || goal.Id == onlyGoalId).ToArray())
        {
            var cacheEvidenceKey = string.Empty;
            if (cache is not null && IsTerminalSweepStatus(originalGoal.Status))
            {
                cacheEvidenceKey = branchFactIndex.BuildGoalEvidenceKey(originalGoal);
            }

            if (cache is not null &&
                cacheEvidenceKey.Length > 0 &&
                cache.TryMarkHit(kernel, executionDirectory, originalGoal, cacheEvidenceKey, cacheSweepFacts))
            {
                cacheHits++;
                continue;
            }

            if (cache is not null && IsTerminalSweepStatus(originalGoal.Status))
            {
                cacheMisses++;
            }

            sweptGoalIds.Add(originalGoal.Id);
            var repairs = new List<TerminalGoalSweepRepair>();
            var blockers = new List<TerminalGoalSweepBlocker>();
            var prefix = originalGoal.Id.Value[..Math.Min(8, originalGoal.Id.Value.Length)];

            if (originalGoal.Id == mergeEvidenceBoundGoalId)
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "merge-evidence-backfill-bound-exceeded",
                    $"merge evidence matched {mergeEvidenceCandidateCount} non-terminal goals; safety bound is {MaxMergeEvidenceTerminalizationsPerSweep}; no goals were terminalized",
                    "inspect merge-evidence ancestry and rerun conduct",
                    originalGoal.Id,
                    prefix));
            }

            var goalPendingRecovery = kernel.GetGoal(originalGoal.Id);
            var hasPendingAcceptanceEvidence = goalPendingRecovery.OutstandingCriterionEvidenceObligations.Any(item =>
                item.Owner == CriterionEvidenceOwner.Acceptance &&
                item.State == CriterionEvidenceState.Pending &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal));
            var recoveredCriterionEvidence = AcceptanceCriterionEvidenceRecovery.TryRecord(
                kernel,
                goalPendingRecovery,
                executionDirectory,
                orchestratorDirectory,
                branchFactIndex.MainSha,
                branchFactIndex.TryGetGoalBranchTip(goalPendingRecovery.Id),
                mergeTrainAcceptanceStore,
                cohortAcceptanceStore,
                gitRunner);
            if (recoveredCriterionEvidence is { CanComplete: false })
            {
                repairs.Add(new TerminalGoalSweepRepair(
                    "criterion-evidence-recovered",
                    recoveredCriterionEvidence.AuditDetail,
                    "held by remaining criterion evidence"));
                blockers.Add(new TerminalGoalSweepBlocker(
                    "criterion-evidence-recovery-held",
                    recoveredCriterionEvidence.HoldDiagnostic!,
                    $"next {prefix} --full",
                    originalGoal.Id,
                    prefix));
                results.Add(new TerminalGoalSweepGoalResult(originalGoal.Id, prefix, repairs, blockers));
                continue;
            }
            if (recoveredCriterionEvidence is { CanComplete: true })
            {
                var recoveredIntegrateSha = integrationEvidenceByGoal.TryGetValue(originalGoal.Id, out var knownIntegration)
                    ? knownIntegration.IntegrateSha
                    : TryRecoverLandingMerge(executionDirectory, goalPendingRecovery, gitRunner, out var recoveredLanding)
                        ? recoveredLanding.MergeCommitSha
                        : recoveredCriterionEvidence.CandidateSha;
                var recoveredIntegrationEvidence = new GoalIntegrationEvidence(
                    recoveredIntegrateSha,
                    branchFactIndex.MainSha!,
                    $"Recovered criterion evidence for goal/{prefix}");
                var terminalization = TerminalizeFromMergeEvidence(
                    kernel,
                    executionDirectory,
                    goalPendingRecovery,
                    recoveredIntegrationEvidence,
                    attentionStore,
                    repairs,
                    orchestratorDirectory,
                    recoveredCriterionEvidence.AuditDetail);
                resolvedAttentionItemCount += terminalization.ResolvedAttentionItemCount;
                terminalizedGoalCount++;
                terminalizedGoalIds.Add(originalGoal.Id);
                results.Add(new TerminalGoalSweepGoalResult(
                    originalGoal.Id,
                    prefix,
                    repairs,
                    blockers,
                    terminalization.Receipt));
                continue;
            }
            if (hasPendingAcceptanceEvidence && integrationEvidenceByGoal.ContainsKey(originalGoal.Id))
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "criterion-evidence-recovery-unproven",
                    "merge evidence exists, but no deterministic passed full gate for this goal has a certified candidate reachable from main",
                    $"next {prefix} --full",
                    originalGoal.Id,
                    prefix));
                results.Add(new TerminalGoalSweepGoalResult(originalGoal.Id, prefix, repairs, blockers));
                continue;
            }

            if (integrationEvidenceByGoal.TryGetValue(originalGoal.Id, out var integrationEvidence))
            {
                var terminalization = TerminalizeFromMergeEvidence(
                    kernel,
                    executionDirectory,
                    originalGoal,
                    integrationEvidence,
                    attentionStore,
                    repairs,
                    orchestratorDirectory);
                resolvedAttentionItemCount += terminalization.ResolvedAttentionItemCount;
                terminalizedGoalCount++;
                terminalizedGoalIds.Add(originalGoal.Id);
                results.Add(new TerminalGoalSweepGoalResult(
                    originalGoal.Id,
                    prefix,
                    repairs,
                    blockers,
                    terminalization.Receipt));
                continue;
            }

            var reconciled = dispatchRunner.SweepExitedProcesses(kernel, originalGoal.Id);
            if (reconciled > 0)
            {
                repairs.Add(new TerminalGoalSweepRepair(
                    "dispatch-exit-reconciled",
                    $"reconciled {reconciled} exited dispatch artifact(s)",
                    "reconcile"));
            }

            var goal = kernel.GetGoal(originalGoal.Id);
            var branchFacts = branchFactIndex.BuildGoalBranchFacts(goal);
            var hasTerminalTaskDesync = TryBuildTerminalTaskDesyncEvidence(goal, out var desyncEvidence);
            var blockedByDirtyWorktree = false;
            var hasDurableLandingIntent = HasDurableLandingIntentForCleanup(executionDirectory, goal);
            var skipMergedCleanupThisPass = false;

            if (HasStandingTerminalDisposition(executionDirectory, goal, branchFacts))
            {
                continue;
            }

            if ((hasTerminalTaskDesync || AgentOrchestratorKernel.IsReopenProtectedGoalStatus(goal.Status)) &&
                TryBuildTerminalDirtyWorktreeBlocker(goal, executionDirectory, prefix,
                    desyncEvidence, out var dirtyEvidence, out var dirtyCommand))
            {
                blockedByDirtyWorktree = true;
                blockers.Add(new TerminalGoalSweepBlocker(
                    "terminal-dirty-worktree",
                    dirtyEvidence,
                    dirtyCommand,
                    goal.Id,
                    prefix));
            }
            else if (branchFacts.BranchAlreadyLanded && goal.Status == GoalStatus.Verified)
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "verified-merged-branch-missing-integrate-commit",
                    $"verified goal branch {GoalWorktrees.BranchName(goal.Id)} is reachable from main, but no reachable Integrate commit identifies the landing",
                    $"goal-mark-landed {prefix} --confirm-goal-mark-landed",
                    goal.Id,
                    prefix));
            }
            else if (branchFacts.BranchAlreadyLanded)
            {
                if (hasDurableLandingIntent)
                {
                    AddAutoRepairNoopReceiptIfApplicable(executionDirectory, goal, repairs);
                    foreach (var task in goal.Tasks.Where(task =>
                                 task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) &&
                                 (task.LastProcess is not { IsRunning: true } process ||
                                  !process.TrackedProcessIds.Any(IsProcessAlive))).ToArray())
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
                else
                {
                    if (TryAutoRepairMergedBranchLandingIntent(kernel, executionDirectory, goal, gitRunner, repairs))
                    {
                        goal = kernel.GetGoal(originalGoal.Id);
                        hasDurableLandingIntent = true;
                        skipMergedCleanupThisPass = true;
                    }
                    else
                    {
                        blockers.Add(new TerminalGoalSweepBlocker(
                            "landing-intent-auto-repair-failed",
                            $"goal branch {GoalWorktrees.BranchName(goal.Id)} appears merged, but auto-repair could not recover the merge commit",
                            $"goal-mark-landed {prefix} --confirm-goal-mark-landed",
                            goal.Id,
                            prefix));
                    }
                }
            }

            if ((!blockedByDirtyWorktree || AgentOrchestratorKernel.IsReopenProtectedGoalStatus(goal.Status)) &&
                (!branchFacts.BranchAlreadyLanded || AgentOrchestratorKernel.IsReopenProtectedGoalStatus(goal.Status)) &&
                TryBuildTerminalLiveDispatchBlocker(goal, prefix, out var liveDispatchEvidence, out var liveDispatchCommand))
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "terminal-live-dispatch",
                    liveDispatchEvidence,
                    liveDispatchCommand,
                    goal.Id,
                    prefix));
            }
            else if (!blockedByDirtyWorktree &&
                     !branchFacts.BranchAlreadyLanded &&
                     onlyGoalId is null &&
                     !branchFacts.HasRegisteredWorktree &&
                     !branchFacts.HasGoalBranch &&
                     !Directory.Exists(GoalWorktrees.WorktreePath(executionDirectory, goal.Id)) &&
                     TryBuildGlobalStaleTerminalExclusionEvidence(goal, out var staleTerminalExclusionEvidence))
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "stale-terminal-excluded",
                    staleTerminalExclusionEvidence,
                    "excluded",
                    goal.Id,
                    prefix));
            }
            if (!blockedByDirtyWorktree &&
                blockers.Count == 0 &&
                originalGoal.Status is GoalStatus.Cancelled or GoalStatus.Failed or GoalStatus.Superseded &&
                (GoalWorktrees.TryResolve(executionDirectory, goal.Id) is not null ||
                 branchFactIndex.HasGoalBranch(GoalWorktrees.BranchName(goal.Id)) ||
                 Directory.Exists(GoalWorktrees.WorktreePath(executionDirectory, goal.Id))))
            {
                var removeResult = GoalWorktrees.RemoveTerminal(
                    executionDirectory,
                    goal.Id,
                    kernel,
                    cleanupHooks);
                if (!removeResult.IsComplete)
                {
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "terminal-worktree-cleanup-needed",
                        removeResult.Message,
                        removeResult.ResumeCommand ?? $"conduct {prefix} --loop",
                        goal.Id,
                        prefix));
                }
                else
                {
                    repairs.Add(new TerminalGoalSweepRepair(
                        "terminal-worktree-cleanup",
                        removeResult.Message,
                        $"workspace remove {prefix}"));
                }

                AddOwnedEphemeralCleanupRepair(removeResult.OwnedEphemeralCleanup, prefix, repairs);
            }

            if (hasTerminalTaskDesync &&
                AgentOrchestratorKernel.IsReopenProtectedGoalStatus(goal.Status) &&
                blockers.All(blocker => blocker.Kind is not ("terminal-live-dispatch" or "stale-terminal-excluded")))
            {
                CloseStaleTasksOnTerminalGoal(kernel, goal, prefix, desyncEvidence, repairs);
            }
            else if (!blockedByDirtyWorktree &&
                     blockers.Count == 0 &&
                     !branchFacts.BranchAlreadyLanded &&
                     hasTerminalTaskDesync &&
                     kernel.NormalizeGoalLifecycleState(goal.Id, "terminal stale-goal sweep: reopened terminal goal with non-terminal task(s)."))
            {
                repairs.Add(new TerminalGoalSweepRepair(
                    "terminal-task-desync",
                    $"{desyncEvidence}; action=reopen",
                    $"conduct {prefix} --loop"));
                goal = kernel.GetGoal(originalGoal.Id);
                branchFacts = branchFactIndex.BuildGoalBranchFacts(goal);
            }

            var equivalentBranchTip = branchFacts.ContentState == GoalBranchContentState.EquivalentToMain
                ? branchFactIndex.TryGetGoalBranchTip(goal.Id)
                : null;
            if (!blockedByDirtyWorktree &&
                blockers.Count == 0 &&
                goal.Status == GoalStatus.Completed &&
                equivalentBranchTip is not null)
            {
                var removeResult = GoalWorktrees.RemoveSupersededTerminal(
                    executionDirectory,
                    goal.Id,
                    kernel,
                    equivalentBranchTip,
                    branchFacts.HasRegisteredWorktree,
                    branchFacts.HasGoalBranch,
                    cleanupHooks);
                AddOwnedEphemeralCleanupRepair(removeResult.OwnedEphemeralCleanup, prefix, repairs);

                if (removeResult.IsComplete)
                {
                    var evidence = BuildSupersededBranchEvidence(goal, removeResult.Message);
                    RecordTerminalDisposition(
                        kernel,
                        executionDirectory,
                        goal,
                        GoalTerminalDispositionKind.Retired,
                        evidence);
                    repairs.Add(new TerminalGoalSweepRepair(
                        "completed-branch-superseded",
                        evidence,
                        "retired"));
                }
                else
                {
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "completed-branch-superseded",
                        BuildSupersededBranchEvidence(goal, removeResult.Message),
                        $"conduct {prefix} --loop",
                        goal.Id,
                        prefix));
                }

                results.Add(new TerminalGoalSweepGoalResult(originalGoal.Id, prefix, repairs, blockers));
                continue;
            }

            if (!blockedByDirtyWorktree &&
                branchFacts.IsAcceptedOrVerifiedGitGoal &&
                branchFacts.HasGoalBranchArtifact &&
                !branchFacts.BranchAlreadyLanded)
            {
                var contentEquivalent = branchFacts.ContentState == GoalBranchContentState.EquivalentToMain;
                var acceptance = contentEquivalent
                    ? null
                    : GoalAcceptanceStatusProjector.Build(kernel, goal, executionDirectory);
                blockers.Add(new TerminalGoalSweepBlocker(
                    contentEquivalent ? "completed-branch-superseded" : "completed-branch-unmerged",
                    contentEquivalent
                        ? BuildSupersededBranchEvidence(goal, "retirement required")
                        : AppendAcceptanceHold(
                            $"{goal.Status.ToString().ToLowerInvariant()} goal still has unmerged branch {GoalWorktrees.BranchName(goal.Id)}; contentCheck={branchFacts.ContentState.ToString().ToLowerInvariant()}",
                            acceptance),
                    contentEquivalent
                        ? TerminalGoalRemedy.OperatorOnly(
                            goal.Id,
                            prefix,
                            $"conduct {prefix} --loop")
                        : BuildAcceptanceRemedy(kernel, executionDirectory, goal, prefix, branchFactIndex, acceptance)));
                results.Add(new TerminalGoalSweepGoalResult(originalGoal.Id, prefix, repairs, blockers));
                continue;
            }

            if (!blockedByDirtyWorktree &&
                hasDurableLandingIntent &&
                !skipMergedCleanupThisPass &&
                branchFacts.IsCompletedGitGoal)
            {
                var removeResult = GoalWorktrees.RemoveTerminal(
                    executionDirectory,
                    goal.Id,
                    kernel,
                    branchFacts.HasRegisteredWorktree,
                    branchFacts.HasGoalBranch,
                    cleanupHooks);
                if (removeResult.Message.Contains("kept because it has unmerged commits", StringComparison.OrdinalIgnoreCase))
                {
                    var acceptance = GoalAcceptanceStatusProjector.Build(kernel, goal, executionDirectory);
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "completed-branch-unmerged",
                        AppendAcceptanceHold(removeResult.Message, acceptance),
                        BuildAcceptanceRemedy(kernel, executionDirectory, goal, prefix, branchFactIndex, acceptance)));
                }
                else if (!removeResult.IsComplete)
                {
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "completed-worktree-cleanup-needed",
                        removeResult.Message,
                        removeResult.ResumeCommand ?? $"conduct {prefix} --loop",
                        goal.Id,
                        prefix));
                }
                else if (!removeResult.Message.Contains("already clean", StringComparison.OrdinalIgnoreCase))
                {
                    GoalLifecycleEventWriter.RetireDispatchProviderSessions(
                        kernel,
                        goal.Id,
                        DateTimeOffset.UtcNow);
                    GoalOperationJournal.RecordCleanupCompleted(
                        executionDirectory,
                        goal,
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
                var ephemeralCleanup = GoalWorktrees.SweepOwnedEphemeralDirectories(executionDirectory, goal.Id, kernel, cleanupHooks);
                if (!ephemeralCleanup.IsComplete)
                {
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "owned-ephemeral-cleanup-needed",
                        $"owned ephemeral cleanup incomplete; leftovers={string.Join(",", ephemeralCleanup.LeftoverPaths)}",
                        $"conduct {prefix} --loop",
                        goal.Id,
                        prefix));
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
                    ? branchFactIndex.BuildGoalEvidenceKey(currentGoal)
                    : cacheEvidenceKey;
                cache.Record(kernel, executionDirectory, currentGoal, currentEvidenceKey, blockers, cacheSweepFacts);
            }
        }
        goalsTiming.Stop();

        cache?.Flush();
        return new TerminalGoalSweepResult(
            results,
            CountGlobalStaleTerminalExclusions(results),
            cacheHits,
            cacheMisses,
            sweptGoalIds,
            terminalizedGoalCount,
            resolvedAttentionItemCount,
            TerminalizedGoalIds: terminalizedGoalIds)
        {
            GitIndexDurationMs = gitIndexTiming.ElapsedMilliseconds,
            EvidenceDurationMs = evidenceTiming.ElapsedMilliseconds,
            EphemeralDurationMs = ephemeralTiming.ElapsedMilliseconds,
            AttentionDurationMs = attentionTiming.ElapsedMilliseconds,
            MergeEvidenceDurationMs = mergeEvidenceTiming.ElapsedMilliseconds,
            GoalsDurationMs = goalsTiming.ElapsedMilliseconds,
            OwnedRootDurationMs = ownedRootTiming.ElapsedMilliseconds,
            GitSpawnCount = gitSpawnCounter.Value,
            GoalsSweptCount = sweptGoalIds.Count,
            OwnedRoots = ownedRoots
        };
    }

    private static Dictionary<GoalId, GoalIntegrationEvidence> ResolveMergeEvidenceCandidates(
        AgentOrchestratorKernel kernel,
        GoalId? onlyGoalId,
        IGoalIntegrationEvidenceResolver resolver)
    {
        var matches = new Dictionary<GoalId, GoalIntegrationEvidence>();
        foreach (var goal in kernel.Goals.Where(goal => onlyGoalId is null || goal.Id == onlyGoalId))
        {
            if (IsTerminalSweepStatus(goal.Status) ||
                goal.Tasks.Any(task =>
                    task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled or WorkTaskStatus.Failed) ||
                    task.LastProcess is { IsRunning: true }) ||
                !resolver.TryResolve(goal.Id, out var evidence) ||
                evidence is null)
            {
                continue;
            }

            matches[goal.Id] = evidence;
        }

        return matches;
    }

    private static int ResolveAttentionForExistingTerminalGoals(
        AgentOrchestratorKernel kernel,
        GoalId? onlyGoalId,
        ICollaborationItemStore store)
    {
        var openGoalIds = store.GetAttentionQueueAsync()
            .GetAwaiter()
            .GetResult()
            .Where(item => !string.IsNullOrWhiteSpace(item.GoalId))
            .Select(item => item.GoalId!)
            .ToHashSet(StringComparer.Ordinal);
        var resolved = 0;
        foreach (var goal in kernel.Goals.Where(goal =>
                     (onlyGoalId is null || goal.Id == onlyGoalId) &&
                     IsTerminalSweepStatus(goal.Status) &&
                     openGoalIds.Contains(goal.Id.Value)))
        {
            resolved += store.ResolveOpenForGoalAsync(
                    goal.Id.Value,
                    $"goal terminal state reconciled: {goal.Status}")
                .GetAwaiter()
                .GetResult();
        }

        return resolved;
    }

    private static MergeEvidenceTerminalizationResult TerminalizeFromMergeEvidence(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        Goal goal,
        GoalIntegrationEvidence evidence,
        ICollaborationItemStore attentionStore,
        List<TerminalGoalSweepRepair> repairs,
        string orchestratorDirectory,
        string? acceptanceRecoveryAudit = null)
    {
        var priorStatus = goal.Status;
        var detail =
            $"Goal terminalized from merge evidence at {evidence.IntegrateSha}; mainSha={evidence.MainSha}; priorStatus={priorStatus}." +
            (acceptanceRecoveryAudit is null ? string.Empty : $" AcceptanceRecovery={acceptanceRecoveryAudit}.");
        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var recoveringFromReceipt = GoalOperationJournal.HasMergeEvidenceTerminalDisposition(journal, evidence.IntegrateSha);
        var reconciliation = new TerminalGoalSweepReconciliationReceipt(
            evidence.IntegrateSha,
            evidence.MainSha,
            priorStatus,
            GoalWorktrees.TryResolve(executionDirectory, goal.Id),
            GoalOperationJournal.HasCompletedCleanupEvidence(journal),
            GoalTerminalReconciliationEvidenceResolver.ResolveForGoal(
                Path.Combine(orchestratorDirectory, "acceptance-gate-attempts"),
                goal.Id),
            recoveringFromReceipt);
        kernel.CompleteGoalFromMergeEvidence(goal.Id, evidence.IntegrateSha, detail, recoveringFromReceipt);
        if (!recoveringFromReceipt || acceptanceRecoveryAudit is not null)
        {
            GoalOperationJournal.RecordTerminalDisposition(
                executionDirectory,
                goal,
                new GoalTerminalDisposition(
                    GoalTerminalDispositionKind.Landed,
                    detail,
                    GoalTerminalDispositionSource.MergeEvidence,
                    evidence.IntegrateSha,
                    reconciliation));
        }

        GoalLifecycleEventWriter.RetireDispatchProviderSessions(kernel, goal.Id, DateTimeOffset.UtcNow);
        if (!HasMergeEvidenceGoalLandedEvent(orchestratorDirectory, goal.Id, evidence.IntegrateSha))
        {
            kernel.RecordGoalLandedFromMergeEvidence(
                goal.Id,
                GoalWorktrees.BranchName(goal.Id),
                evidence.IntegrateSha,
                evidence.MainSha);
        }

        var resolution = $"goal terminalized from merge evidence at {evidence.IntegrateSha}";
        var resolvedAttention = attentionStore.ResolveOpenForGoalAsync(goal.Id.Value, resolution)
            .GetAwaiter()
            .GetResult();
        repairs.Add(new TerminalGoalSweepRepair(
            "merge-evidence-terminalized",
            $"integrateSha={evidence.IntegrateSha}; mainSha={evidence.MainSha}; priorStatus={priorStatus}; " +
            $"recoveredFromJournalReceipt={recoveringFromReceipt.ToString().ToLowerInvariant()}; resolvedAttentionItems={resolvedAttention}" +
            (acceptanceRecoveryAudit is null ? string.Empty : $"; acceptanceRecovery={acceptanceRecoveryAudit}"),
            "terminalized"));
        return new MergeEvidenceTerminalizationResult(
            resolvedAttention,
            reconciliation);
    }

    private sealed record MergeEvidenceTerminalizationResult(
        int ResolvedAttentionItemCount,
        TerminalGoalSweepReconciliationReceipt Receipt);

    private static bool HasMergeEvidenceGoalLandedEvent(
        string orchestratorDirectory,
        GoalId goalId,
        string integrateSha)
    {
        var path = Path.Combine(orchestratorDirectory, "goal-events", $"{goalId.Value}.jsonl");
        if (!File.Exists(path))
        {
            return false;
        }

        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("eventType", out var eventType) &&
                    string.Equals(eventType.GetString(), "GoalLanded", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("source", out var source) &&
                    string.Equals(source.GetString(), "merge-evidence", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("integrateSha", out var recordedSha) &&
                    string.Equals(recordedSha.GetString(), integrateSha, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // A malformed historical event cannot prove that this effect already completed.
            }
        }

        return false;
    }

    private static IReadOnlyList<string> EnumerateEphemeralDirectories(string executionDirectory)
    {
        var root = Path.GetFullPath(executionDirectory);
        var directories = new List<string>();
        foreach (var rootName in new[] { ".t", ".scratch" })
        {
            var ephemeralRoot = Path.Combine(root, rootName);
            if (Directory.Exists(ephemeralRoot))
            {
                directories.AddRange(Directory.EnumerateDirectories(ephemeralRoot));
            }
        }

        return directories;
    }

    private static bool HasStandingTerminalDisposition(
        string executionDirectory,
        Goal goal,
        GoalBranchFacts branchFacts)
    {
        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        return ((goal.Status == GoalStatus.Cancelled || !branchFacts.HasGoalBranchArtifact) &&
                GoalOperationJournal.HasRetiredTerminalDisposition(journal)) ||
            (goal.Status == GoalStatus.Completed &&
             !branchFacts.HasGoalBranchArtifact &&
             GoalOperationJournal.HasMergeEvidenceTerminalDisposition(journal));
    }

    private static bool HasDurableLandingIntentForCleanup(string executionDirectory, Goal goal) =>
        goal.Status != GoalStatus.Cancelled &&
        GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(executionDirectory, goal.Id));

    private static void AddAutoRepairNoopReceiptIfApplicable(
        string executionDirectory,
        Goal goal,
        List<TerminalGoalSweepRepair> repairs)
    {
        var intent = GoalOperationJournal.TryGetLatestLandingIntent(GoalOperationJournal.Read(executionDirectory, goal.Id));
        if (intent is null ||
            !intent.Source.Equals("auto-repair", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var repairedAtUtc = DateTimeOffset.UtcNow;
        repairs.Add(new TerminalGoalSweepRepair(
            "landing-intent-auto-repair-noop",
            BuildAutoRepairReceipt(goal.Id, intent.MergeCommitSha, repairedAtUtc),
            $"workspace remove {goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)]}"));
    }

    private static bool TryAutoRepairMergedBranchLandingIntent(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        Goal goal,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner,
        List<TerminalGoalSweepRepair> repairs)
    {
        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        if (GoalOperationJournal.TryGetLatestLandingIntent(journal) is not null)
        {
            AddAutoRepairNoopReceiptIfApplicable(executionDirectory, goal, repairs);
            return true;
        }

        if (!TryRecoverLandingMerge(executionDirectory, goal, gitRunner, out var recovered))
        {
            return false;
        }

        var goalBranch = GoalWorktrees.BranchName(goal.Id);
        GoalOperationJournal.RecordLandingIntent(
            executionDirectory,
            goal,
            goalBranch,
            LandingExecutor.IntegrationBranchName,
            recovered.MergeCommitSha,
            "auto-repair",
            recovered.CommitAtUtc);

        var repairedAtUtc = DateTimeOffset.UtcNow;
        RecordTerminalDisposition(
            kernel,
            executionDirectory,
            goal,
            GoalTerminalDispositionKind.Landed,
            $"Terminal sweep auto-repaired missing landing intent: {BuildAutoRepairReceipt(goal.Id, recovered.MergeCommitSha, repairedAtUtc)}");
        repairs.Add(new TerminalGoalSweepRepair(
            "landing-intent-auto-repair",
            BuildAutoRepairReceipt(goal.Id, recovered.MergeCommitSha, repairedAtUtc),
            $"workspace remove {goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)]}"));
        return true;
    }

    private static string BuildAutoRepairReceipt(GoalId goalId, string mergeCommitSha, DateTimeOffset repairedAtUtc) =>
        $"goalId={goalId.Value}; mergeCommitSha={mergeCommitSha}; repairedAtUtc={repairedAtUtc:O}; source=auto-repair";

    private static bool TryRecoverLandingMerge(
        string executionDirectory,
        Goal goal,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner,
        out RecoveredLandingMerge recovered)
    {
        recovered = default;
        if (!TryResolveMergedBranchTip(executionDirectory, goal, gitRunner, out var branchTip))
        {
            return false;
        }

        var ancestry = RunGit(gitRunner, executionDirectory, "log", "--format=%H", "--reverse", "--ancestry-path", $"{branchTip}..main");
        var mergeCommitSha = ancestry.ExitCode == 0
            ? ancestry.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(mergeCommitSha))
        {
            var tipLog = RunGit(gitRunner, executionDirectory, "log", "--format=%H", "-n", "1", branchTip);
            mergeCommitSha = tipLog.ExitCode == 0 ? tipLog.Output.Trim() : branchTip;
        }

        if (string.IsNullOrWhiteSpace(mergeCommitSha))
        {
            return false;
        }

        recovered = new RecoveredLandingMerge(
            mergeCommitSha.Trim(),
            TryGetCommitUtc(executionDirectory, mergeCommitSha.Trim(), gitRunner) ?? DateTimeOffset.UtcNow);
        return true;
    }

    private static bool TryResolveMergedBranchTip(
        string executionDirectory,
        Goal goal,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner,
        out string branchTip)
    {
        var branch = GoalWorktrees.BranchName(goal.Id);
        var branchResult = RunGit(gitRunner, executionDirectory, "rev-parse", $"refs/heads/{branch}");
        if (branchResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(branchResult.Output))
        {
            branchTip = branchResult.Output.Trim();
            return true;
        }

        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        if (worktree is not null)
        {
            var worktreeResult = RunGit(gitRunner, worktree, "rev-parse", "HEAD");
            if (worktreeResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(worktreeResult.Output))
            {
                branchTip = worktreeResult.Output.Trim();
                return true;
            }
        }

        branchTip = string.Empty;
        return false;
    }

    private static DateTimeOffset? TryGetCommitUtc(
        string executionDirectory,
        string commitSha,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        var result = RunGit(gitRunner, executionDirectory, "show", "-s", "--format=%cI", commitSha);
        return result.ExitCode == 0 &&
            DateTimeOffset.TryParse(
                result.Output.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            ? parsed
            : null;
    }

    private static GitCli.GitResult RunGit(
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner,
        string executionDirectory,
        params string[] args) =>
        gitRunner(executionDirectory, args);

    private readonly record struct RecoveredLandingMerge(string MergeCommitSha, DateTimeOffset CommitAtUtc);

    private static void RecordTerminalDisposition(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        Goal goal,
        GoalTerminalDispositionKind kind,
        string detail)
    {
        kernel.CompleteGoal(goal.Id, detail);
        var source = kind == GoalTerminalDispositionKind.Landed &&
            GoalOperationJournal.HasMergeEvidenceTerminalDisposition(
                GoalOperationJournal.Read(executionDirectory, goal.Id))
            ? GoalTerminalDispositionSource.MergeEvidence
            : GoalTerminalDispositionSource.General;
        GoalOperationJournal.RecordTerminalDisposition(
            executionDirectory,
            goal,
            new GoalTerminalDisposition(kind, detail, source));
        GoalLifecycleEventWriter.RetireDispatchProviderSessions(kernel, goal.Id, DateTimeOffset.UtcNow);
    }

    public static TerminalGoalSweepResult Diagnose(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null)
    {
        var results = new List<TerminalGoalSweepGoalResult>();
        var branchFactIndex = GoalGitFactIndex.Build(executionDirectory, gitRunner);

        foreach (var goal in kernel.Goals.Where(goal => onlyGoalId is null || goal.Id == onlyGoalId).ToArray())
        {
            var blockers = new List<TerminalGoalSweepBlocker>();
            var prefix = goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)];

            if (TryBuildTerminalLiveDispatchBlocker(goal, prefix, out var liveDispatchEvidence, out var liveDispatchCommand))
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "terminal-live-dispatch",
                    liveDispatchEvidence,
                    liveDispatchCommand,
                    goal.Id,
                    prefix));
            }

            var branchFacts = branchFactIndex.BuildGoalBranchFacts(goal);
            if (branchFacts.IsAcceptedOrVerifiedGitGoal &&
                branchFacts.HasGoalBranchArtifact &&
                !branchFacts.BranchAlreadyLanded)
            {
                var contentEquivalent = branchFacts.ContentState == GoalBranchContentState.EquivalentToMain;
                var acceptance = contentEquivalent
                    ? null
                    : GoalAcceptanceStatusProjector.Build(kernel, goal, executionDirectory);
                blockers.Add(new TerminalGoalSweepBlocker(
                    contentEquivalent ? "completed-branch-superseded" : "completed-branch-unmerged",
                    contentEquivalent
                        ? BuildSupersededBranchEvidence(goal, "retirement required")
                        : AppendAcceptanceHold(
                            $"{goal.Status.ToString().ToLowerInvariant()} goal still has unmerged branch {GoalWorktrees.BranchName(goal.Id)}; contentCheck={branchFacts.ContentState.ToString().ToLowerInvariant()}",
                            acceptance),
                    contentEquivalent
                        ? TerminalGoalRemedy.OperatorOnly(
                            goal.Id,
                            prefix,
                            $"conduct {prefix} --loop")
                        : BuildAcceptanceRemedy(kernel, executionDirectory, goal, prefix, branchFactIndex, acceptance)));
            }

            if (blockers.Count > 0)
            {
                results.Add(new TerminalGoalSweepGoalResult(goal.Id, prefix, [], blockers));
            }
        }

        return new TerminalGoalSweepResult(
            results,
            CountGlobalStaleTerminalExclusions(results),
            SweptGoalIds: kernel.Goals.Where(goal => onlyGoalId is null || goal.Id == onlyGoalId).Select(goal => goal.Id).ToArray());
    }

    private static string AppendAcceptanceHold(
        string evidence,
        GoalAcceptanceSummary? acceptance) =>
        string.IsNullOrWhiteSpace(acceptance?.AcceptanceHoldDescription)
            ? evidence
            : $"{evidence}; {acceptance.AcceptanceHoldDescription}";

    private static TerminalGoalRemedy BuildAcceptanceRemedy(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        Goal goal,
        string prefix,
        GoalGitFactIndex branchFactIndex,
        GoalAcceptanceSummary? acceptance = null)
    {
        if (acceptance is { PendingHumanInputCount: > 0 } &&
            GoalAcceptanceStatusProjector.BuildPendingHumanWaitAttentionCommand(
                kernel,
                goal.Id,
                prefix) is { } attentionCommand)
        {
            return TerminalGoalRemedy.OperatorOnly(goal.Id, prefix, attentionCommand);
        }

        var branchHeadSha = branchFactIndex.TryGetGoalBranchTip(goal.Id);
        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var passedGate = GoalOperationJournal.NewestPassedGateForBranch(
            journal,
            branchHeadSha);
        var newerGuardAbort = journal.Entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(branchHeadSha) &&
                string.Equals(entry.BranchHeadSha?.Trim(), branchHeadSha?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.AcceptanceOutcome, "aborted:state-guard", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.At)
            .FirstOrDefault(entry => passedGate is null || entry.At >= passedGate.At);
        if (newerGuardAbort is not null)
        {
            var guidance = passedGate is null
                ? $"Quiesce conductor mutations for goal {prefix}, then run acceptance {prefix}; no passing gate receipt was recorded for this candidate."
                : $"Quiesce conductor mutations for goal {prefix}, then run acceptance {prefix}; prior passing gate receipt {passedGate.IdempotencyKey} remains recorded.";
            return TerminalGoalRemedy.OperatorOnly(
                goal.Id,
                prefix,
                guidance);
        }

        var artifact = passedGate is null || string.IsNullOrWhiteSpace(branchHeadSha)
            ? null
            : new TerminalGoalGateArtifact(
                passedGate.IdempotencyKey,
                branchHeadSha,
                passedGate.AcceptanceOutcome ?? "gate-passed");
        return TerminalGoalRemedy.Acceptance(goal.Id, prefix, artifact);
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
        var nonTerminalTasks = goal.Tasks
            .Where(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
            .Select(task => $"{task.Id.Value[..8]}:{task.Status}")
            .ToArray();
        if (dispatchableTasks.Length == 0 &&
            (!AgentOrchestratorKernel.IsReopenProtectedGoalStatus(goal.Status) || nonTerminalTasks.Length == 0))
        {
            return false;
        }

        evidence = $"goalState={goal.Status}; dispatchableTasks={string.Join(",", dispatchableTasks)}; nonTerminalTasks={string.Join(",", nonTerminalTasks)}";
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

        evidence = $"{desyncEvidence}{(desyncEvidence.Length > 0 ? "; " : string.Empty)}worktreeDirty=true; worktree={worktree}";
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
            .Where(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) &&
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
        status == GoalStatus.Failed;

    internal static bool IsStaleTerminalAssignedTaskStatus(WorkTaskStatus status) =>
        status is WorkTaskStatus.Assigned or WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman;

    private static bool IsTerminalCleanupBlockingBlocker(TerminalGoalSweepBlocker blocker) =>
        blocker.Kind is "terminal-dirty-worktree" or
            "terminal-live-dispatch" or
            "stale-terminal-excluded" or
            "completed-branch-unmerged" or
            "completed-branch-superseded";

    private static string BuildSupersededBranchEvidence(Goal goal, string cleanupDetail) =>
        $"completed goal branch {GoalWorktrees.BranchName(goal.Id)} is not merged by ancestry, but git cherry found every branch commit already upstream; action=retire; cleanup={cleanupDetail}";

    private static int CountGlobalStaleTerminalExclusions(IEnumerable<TerminalGoalSweepGoalResult> results) =>
        results.Sum(goal => goal.Blockers.Count(blocker => blocker.Kind == "stale-terminal-excluded"));

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (ProcessProbeFailure.IsNotLive(exception))
        {
            return false;
        }
    }
}
