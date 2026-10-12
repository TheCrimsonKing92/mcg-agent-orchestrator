using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// One repository per class, with filesystem-only restoration between sequential cases.
// The helper inherits construction methods, but never acquires a class-lifetime capacity lease.
public sealed class MergeTrainDeferralSharedFixture : IDisposable
{
    private readonly Dictionary<string, byte[]> baselineFiles;
    private readonly string[] baselineDirectories;
    private readonly byte[] passingTrx;
    private bool disposed;

    public string Repository { get; }
    public AgentOrchestratorKernel Kernel { get; } = new();
    public Goal[] Goals { get; }
    public int BuildCount { get; private set; }
    internal int BuildGitProbeLaunchCount { get; }
    internal string OrchestratorDirectory => OrchestratorWorkspace.ForDirectory(Repository).OrchestratorDirectory;
    internal string DatabasePath => Path.Combine(OrchestratorDirectory, "merge-train-acceptance.db");
    internal string MainRevision { get; }
    internal string[] CandidateRevisions { get; }

    public MergeTrainDeferralSharedFixture()
    {
        using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
        {
            Repository = Helpers.CreateRepository();
            try
            {
                Goals = Helpers.BuildCandidates(Repository, Kernel);
                passingTrx = File.ReadAllBytes(Helpers.WriteTrx(Repository));
                BuildCount++;
                BuildGitProbeLaunchCount = launches.Count;
            }
            catch
            {
                Helpers.RemoveRepository(Repository);
                throw;
            }
        }

        try
        {
            // Initialize all setup-created stores before taking their migrated, empty bytes.
            using (PrepareDeferral(3)) { }
            foreach (var directory in new[] { "parallel-attempts", "grouped-gate-attempts", "test-dotnet" })
                Directory.CreateDirectory(Path.Combine(OrchestratorDirectory, directory));
            baselineDirectories = Directory.GetDirectories(OrchestratorDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(OrchestratorDirectory, path)).ToArray();
            baselineFiles = Directory.GetFiles(OrchestratorDirectory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(OrchestratorDirectory, path), File.ReadAllBytes);
            MainRevision = Helpers.Revision(Repository, "main");
            CandidateRevisions = Goals.Select(goal => Helpers.CandidateRevision(Repository, goal)).ToArray();
        }
        catch
        {
            Helpers.RemoveRepository(Repository);
            throw;
        }
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // The existing assertions use the default pool. Close only this fixture's pool;
        // production stores use unpooled connections, and no case reader is still active.
        ClearReceiptPool();
        // This root is fixture-owned. Restore every store, sidecar and evidence file, as well
        // as parallel-attempts, grouped-gate-attempts and test-dotnet. Never copy linked worktrees.
        Directory.Delete(OrchestratorDirectory, recursive: true);
        Directory.CreateDirectory(OrchestratorDirectory);
        foreach (var directory in baselineDirectories)
            Directory.CreateDirectory(Path.Combine(OrchestratorDirectory, directory));
        foreach (var (path, bytes) in baselineFiles)
            File.WriteAllBytes(Path.Combine(OrchestratorDirectory, path), bytes);
        File.WriteAllBytes(Path.Combine(Repository, "train-green.trx"), passingTrx);
    }

    internal string[] DirectoryListing() => Directory.GetFileSystemEntries(OrchestratorDirectory, "*",
        SearchOption.AllDirectories).Select(path => Path.GetRelativePath(OrchestratorDirectory, path))
        .Order(StringComparer.Ordinal).ToArray();

    internal string[] ReadRevisions() => [Helpers.Revision(Repository, "main"),
        .. Goals.Select(goal => Helpers.CandidateRevision(Repository, goal))];

    internal DeferralScenario PrepareDeferral(int busyRounds) =>
        Helpers.Prepare(Repository, Kernel, Goals, busyRounds);

    internal DeferralSnapshot RunDeferral(int busyRounds)
    {
        Reset();
        using var scenario = PrepareDeferral(busyRounds);
        return ExecuteDeferral(Repository, Goals, scenario);
    }

    private static DeferralSnapshot ExecuteDeferral(string repo, Goal[] goals, DeferralScenario scenario)
    {
        var run = scenario.Driver.RunMergeTrain(scenario.Selection, goals,
            ConductorAutonomyPolicy.Permissive, gateOnly: true);
        Helpers.AssertNoTrainWorkspaces(repo);
        var detail = run.Detail.Replace(scenario.Identity!, "<train>", StringComparison.Ordinal);
        if (run.RecordedReceipt is { } receipt)
            detail = detail.Replace(receipt.ReceiptId, "<receipt>", StringComparison.Ordinal);
        var members = scenario.Selection.Members.Where(member => run.MemberResults.ContainsKey(member.GoalId.Value))
            .Select(member => run.MemberResults[member.GoalId.Value]).ToArray();
        return new(detail, scenario.Acquisitions, scenario.Delays, run.Ejections.Count,
            members.Select(member => member.Outcome.GetType()).ToArray(),
            members.Select(member => (member.Outcome as ConductorAdvanceOutcome.Held)?.State).ToArray(),
            run.Receipt is null, run.RecordedReceipt is null, run.RecordedReceipt?.Outcome,
            scenario.Verifier.RunCount);
    }

    internal static DeferralSnapshot RunFreshDeferral(int busyRounds) => WithFreshTrain(busyRounds);

    // Retain the original per-case repository lifetime as the independent equivalence arm.
    private static DeferralSnapshot WithFreshTrain(int busyRounds)
    {
        var repo = Helpers.CreateRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = Helpers.BuildCandidates(repo, kernel);
            Helpers.WriteTrx(repo);
            using var scenario = Helpers.Prepare(repo, kernel, goals, busyRounds);
            return ExecuteDeferral(repo, goals, scenario);
        }
        finally { Helpers.RemoveRepository(repo); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearReceiptPool();
        Helpers.RemoveRepository(Repository);
    }

    private void ClearReceiptPool()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        SqliteConnection.ClearPool(connection);
    }

    internal sealed record DeferralSnapshot(string Detail, int Acquisitions, int Delays, int Ejections,
        Type[] OutcomeTypes, GoalLifecycleState?[] HeldStates, bool ReceiptIsNull,
        bool RecordedReceiptIsNull, MergeTrainGateOutcome? ReceiptOutcome, int VerifierRunCount);

    internal sealed class DeferralScenario : IDisposable
    {
        internal ConductorDriver Driver { get; set; } = null!;
        internal ConductorMergeTrainSelection Selection { get; set; } = null!;
        internal GateVerifier Verifier { get; set; } = null!;
        internal int Acquisitions;
        internal int Delays;
        internal string? Identity;
        internal DotnetBuildEnvironmentLease? Lease;
        public void Dispose() => Lease?.Dispose();
    }

    internal sealed class GateVerifier(AcceptanceVerificationResult result) : IGoalAcceptanceVerifier
    {
        internal int RunCount;
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            Assert.NotNull(stableSlotLease);
            Assert.True(stableSlotLease.IsExecutionLockHeld);
            RunCount++;
            return Task.FromResult(result);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) =>
            throw new NotSupportedException("Focused evidence is not used by the train gate fixture.");
    }

    private sealed class Helpers : AcceptanceCohortWorkflowTests
    {
        internal static string CreateRepository() => CreateReducedAcceptanceCohortRepository();
        internal static void RemoveRepository(string repo) => DeleteDirectory(repo);
        internal static string WriteTrx(string repo) => WritePassingTrx(repo, "train-green.trx");
        internal static string Revision(string repo, string revision) => RunGitOutput(repo, "rev-parse", revision).Trim();
        internal static string CandidateRevision(string repo, Goal goal) =>
            Revision(GoalWorktrees.TryResolve(repo, goal.Id)!, "HEAD");
        internal static void AssertNoTrainWorkspaces(string repo) => AssertNoMergeTrainWorkspaces(repo);

        internal static Goal[] BuildCandidates(string repo, AgentOrchestratorKernel kernel)
        {
            AddAcceptanceManifest(repo);
            var paths = new[] { "tests/Mcg.AgentOrchestrator.Core.Tests/First.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Second.cs",
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Third.cs" };
            return paths.Select((path, index) =>
            {
                var goal = CreateCompletedGoal(kernel, $"Train member {index}", repo);
                var candidate = CreateWorktreeCandidate(repo, goal.Id, path, $"member {index}");
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                    { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
                return goal;
            }).ToArray();
        }

        internal static DeferralScenario Prepare(string repo, AgentOrchestratorKernel kernel, Goal[] goals,
            int busyRounds)
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var cleanup = CreateIsolatedCleanupContext(repo).Hooks;
            var verifier = new GateVerifier(new(true, false, 0, null,
                TestResultPaths: [Path.Combine(repo, "train-green.trx")]));
            var scenario = new DeferralScenario { Verifier = verifier };
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(workspace.OrchestratorDirectory, "parallel-attempts"), workspace.IntegrationBranch,
                buildStorageRoot: cleanup.BuildStorageRoot,
                acquireCohortStableSlotRound: (identity, label, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    scenario.Identity ??= identity;
                    Assert.Equal(scenario.Identity, identity);
                    Assert.StartsWith("merge-train:", label);
                    if (++scenario.Acquisitions <= busyRounds)
                        throw new DotnetBuildSlotsBusyException(new("first-available-stable-slot", []));
                    return scenario.Lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                        TimeSpan.Zero, storageRoot: cleanup.BuildStorageRoot, holderLabel: label,
                        cancellationToken: token);
                }, cohortStableSlotRoundDelay: (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    scenario.Delays++;
                    return Task.CompletedTask;
                });
            scenario.Driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), coordinator, cleanup);
            scenario.Selection = ProjectTrainSelection(scenario.Driver, goals);
            return scenario;
        }
    }
}
