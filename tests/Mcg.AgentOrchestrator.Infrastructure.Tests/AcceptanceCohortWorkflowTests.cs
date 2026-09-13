using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public abstract class AcceptanceCohortWorkflowTests : GoalWorktreeTestBase
{

    private protected static void AssertEarlyInfrastructureResultWithInvalidPathPersistsStructuredFailure(
        bool skipped,
        int? exitCode,
        string outputTail)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First early invalid-path cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second early invalid-path cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: skipped,
                    ExitCode: exitCode,
                    OutputTail: outputTail,
                    Checks: [new AcceptanceCheckResult("combined", false, exitCode, outputTail)],
                    TestResultPaths: ["bad\0path.trx"]),
                exception: null);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var mainBefore = RunGitOutput(repo, "rev-parse", "main").Trim();
            ConductorAcceptanceCohortRunResult? result = null;

            var exception = Record.Exception(() => result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive));

            Assert.Null(exception);
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(result?.Receipt);
            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, receipt.Outcome);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.ResultPathInvalid, receipt.InfrastructureReasonCode);
            Assert.Empty(receipt.GateTestResultPaths);
            Assert.All(result!.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            AssertNoCohortWorkspaces(repo);

            var persisted = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"))
                .TryReadReceipt(receipt.Identity.Value);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.ResultPathInvalid, persisted?.InfrastructureReasonCode);
            Assert.Empty(persisted!.GateTestResultPaths);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private protected static void AddAcceptanceManifest(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, "config"));
        File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
        RunGit(repo, "add", "config/acceptance-manifest.json");
        RunGit(repo, "commit", "-m", "Add manifest");
    }

    private protected static void AddSourceSizeAuthority(
        string repo,
        params (string Path, int Ceiling)[] ceilings)
    {
        var authorityPath = Path.Combine(
            repo,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
        File.WriteAllLines(
            authorityPath,
            ceilings.Select(ceiling =>
                $"new SourceSizeCeiling(\"{ceiling.Path}\", {ceiling.Ceiling})"));
        RunGit(repo, "add", SourceSizeRatchet.SourcePath);
        RunGit(repo, "commit", "-m", "Add source size authority");
    }

    private protected static string ValidPassingTrx() => """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="1" testName="Passes" outcome="Passed" />
          </Results>
          <ResultSummary outcome="Completed">
            <Counters total="1" executed="1" passed="1" failed="0" />
          </ResultSummary>
        </TestRun>
        """;

    private protected static string WritePassingTrx(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllText(path, ValidPassingTrx());
        return path;
    }

    private protected static string WriteFailingTrx(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllText(path, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="Fails" outcome="Failed" />
              </Results>
              <ResultSummary outcome="Failed">
                <Counters total="1" executed="1" passed="0" failed="1" />
              </ResultSummary>
            </TestRun>
            """);
        return path;
    }

    private protected static AcceptanceVerificationResult FailedVerification(
        string directory,
        string trxFileName,
        string checkName) => new(
            Passed: false,
            Skipped: false,
            ExitCode: 1,
            OutputTail: $"{checkName} failed",
            Checks: [new AcceptanceCheckResult(checkName, false, 1, $"{checkName} failed")],
            TestResultPaths: [WriteFailingTrx(directory, trxFileName)]);

    private protected static string CreateAcceptanceCohortRepository()
    {
        var repo = CreateSeededRepository();
        RunGit(repo, "branch", "-M", "main");
        return repo;
    }

    private protected static void AssertNoCohortWorkspaces(string repo)
    {
        var worktreeRoot = Path.Combine(repo, GoalWorktrees.DirectoryName);
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "c-*"));
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "p-*"));
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "cohort-*"));
    }

    private protected static string CreateWorktreeCandidate(
        string repo,
        GoalId goalId,
        string relativePath,
        string contents)
    {
        var worktree = GoalWorktrees.Ensure(repo, goalId);
        var path = Path.Combine(worktree, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        RunGit(worktree, "add", relativePath);
        RunGit(worktree, "commit", "-m", $"Candidate {goalId.Value[..8]}");
        return RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
    }

    private protected static ConductorAcceptanceCohortSelection ProjectSelection(
        ConductorDriver driver,
        Goal first,
        Goal second)
    {
        var members = new[] { first, second }
            .Select(goal =>
            {
                var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(driver.ExecutionDirectory, goal.Id));
                var revisions = ConductorGitRevisionReader.ReadRequiredPair(worktree);
                Assert.False(string.IsNullOrWhiteSpace(revisions.BranchRevision));
                Assert.False(string.IsNullOrWhiteSpace(revisions.MainRevision));
                var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive);
                var ready = result as GateReadyCandidateProjectionResult.Ready;
                Assert.True(
                    ready is not null,
                    result is GateReadyCandidateProjectionResult.Excluded excluded
                        ? $"Expected Ready projection for {goal.Id.Value[..8]}, but got {excluded.Reason}."
                        : $"Expected Ready projection for {goal.Id.Value[..8]}.");
                return ready.Projection;
            })
            .ToArray();
        return new ConductorAcceptanceCohortSelection(members, []);
    }

    private protected static ConductorMergeTrainSelection ProjectTrainSelection(
        ConductorDriver driver,
        params Goal[] goals)
    {
        var candidates = goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(
            goal.Id,
            driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive))).ToArray();
        var selection = ConductorMergeTrainSelector.Select(candidates);
        Assert.True(selection is not null,
            $"Expected a merge train from candidate projections: {string.Join(" | ", candidates.Select(candidate => candidate.ToString()))}");
        return selection;
    }

    private protected static void AssertNoMergeTrainWorkspaces(string repo)
    {
        var worktreeRoot = Path.Combine(repo, GoalWorktrees.DirectoryName);
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "t-*"));
    }

    private protected static (GoalId GoalId, string Revision) CreateCandidate(
        string repo,
        string goalValue,
        string relativePath,
        string contents)
    {
        var goalId = new GoalId(goalValue);
        RunGit(repo, "checkout", "-b", GoalWorktrees.BranchName(goalId), "main");
        var path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        RunGit(repo, "add", relativePath);
        RunGit(repo, "commit", "-m", $"Candidate {goalValue[..8]}");
        var revision = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
        RunGit(repo, "checkout", "main");
        return (goalId, revision);
    }

    private protected static AcceptanceCohortMemberBinding Bind(
        GoalId goalId,
        string revision,
        string path,
        string resource) => new(
            goalId,
            revision,
            revision,
            [path],
            [resource],
            ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto,
             GateReadyMergeStatus.Clean.ToString(),
             GateReadyMergeReason.NoConflictsDetected.ToString());

    private protected static MergeTrainMemberBinding TrainBind(
        GoalId goalId,
        string revision,
        string path,
        string resource) => new(
            goalId,
            revision,
            revision,
            [path],
            [resource],
            ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto,
            GateReadyMergeStatus.Clean.ToString(),
             GateReadyMergeReason.NoConflictsDetected.ToString());

    private protected sealed class ModeledIntegrityLabeler(bool failLowSet = false) : IWorkerIntegrityLabeler
    {
        private readonly Dictionary<string, IntegrityLabelState> _states =
            new(StringComparer.OrdinalIgnoreCase);

        internal List<(string Kind, string Path)> Operations { get; } = [];

        internal List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];

        public IntegrityLabelState Query(string path)
        {
            var normalized = Path.GetFullPath(path);
            Operations.Add(("query", normalized));
            return ResolveState(normalized) ??
                new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
        }

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            var normalized = Path.GetFullPath(path);
            Operations.Add(("set", normalized));
            SetCalls.Add((normalized, level, recursive));
            var low = level.EndsWith('L');
            if (low && failLowSet)
            {
                return false;
            }

            _states[normalized] = new IntegrityLabelState(
                Exists: true,
                Low: low,
                Inheritable: level.Contains("(OI)(CI)", StringComparison.Ordinal),
                Medium: level.EndsWith('M'));
            return true;
        }

        internal void Seed(string path, IntegrityLabelState state) =>
            _states[Path.GetFullPath(path)] = state;

        internal int FindSetIndex(string path, string level, bool recursive)
        {
            var normalized = Path.GetFullPath(path);
            return SetCalls.FindIndex(call =>
                string.Equals(call.Path, normalized, StringComparison.OrdinalIgnoreCase) &&
                call.Level == level &&
                call.Recursive == recursive);
        }

        internal bool WouldDenyLowWrite(string path)
        {
            var current = Path.GetFullPath(path);
            return ResolveState(current) is not { Low: true };
        }

        private IntegrityLabelState? ResolveState(string path)
        {
            var current = Path.GetFullPath(path);
            var exact = true;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (_states.TryGetValue(current, out var state) && (exact || state.Inheritable))
                {
                    return state;
                }

                exact = false;
                current = Path.GetDirectoryName(current) ?? string.Empty;
            }

            return null;
        }
    }

    internal sealed class BlockingAcceptanceVerifier(
        ManualResetEventSlim started,
        ManualResetEventSlim release,
        AcceptanceVerificationResult result) : IGoalAcceptanceVerifier
    {
        private int _runCount;

        internal int RunCount => Volatile.Read(ref _runCount);
        internal bool StableSlotLeaseObserved { get; private set; }
        internal string StableSlotRootPath { get; private set; } = string.Empty;
        internal string WorktreePath { get; private set; } = string.Empty;

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runCount);
            WorktreePath = worktreePath;
            StableSlotLeaseObserved = stableSlotLease is not null && stableSlotIndex is not null;
            StableSlotRootPath = stableSlotLease?.Environment.RootPath ?? string.Empty;
            var now = DateTimeOffset.UtcNow;
            typeof(GoalAcceptanceVerifier)
                .GetMethod("EmitGateProgress", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null,
                [
                    new AcceptanceGateProgress(
                        GoalId: goalId?.Value,
                        Phase: "controlled-cohort",
                        CurrentTarget: "blocking-verifier",
                        SlotIndex: stableSlotIndex,
                        ProcessId: Environment.ProcessId,
                        ChildProcessId: null,
                        StartedAt: now,
                        LastObservedAt: now,
                        LastProgressAt: now,
                        Elapsed: TimeSpan.Zero,
                        OutputBytes: 0,
                        HeartbeatPath: Path.Combine(worktreePath, "controlled-heartbeat.json"))
                ]);
            started.Set();
            release.Wait(cancellationToken);
            return Task.FromResult(result);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Focused evidence is not used by the blocking cohort fixture.");
    }

    private protected sealed class SequenceAcceptanceVerifier(
        IReadOnlyList<AcceptanceVerificationResult> results) : IGoalAcceptanceVerifier
    {
        private int _nextResult;

        internal int RunCount => _nextResult;
        internal List<GoalId?> GoalIds { get; } = [];
        internal List<IReadOnlyList<string>> ChangedFiles { get; } = [];
        internal List<IReadOnlyList<string>> ObservedBuildPaths { get; } = [];

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            GoalIds.Add(goalId);
            ChangedFiles.Add(changedFiles?.ToArray() ?? []);
            ObservedBuildPaths.Add(stableSlotLease is null ? [] :
            [
                stableSlotLease.Environment.RootPath,
                stableSlotLease.Environment.ArtifactsPath,
                stableSlotLease.Environment.ExecutionLockPath
            ]);
            Assert.True(_nextResult < results.Count, "The cohort invoked the acceptance verifier more times than expected.");
            return Task.FromResult(results[_nextResult++]);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Focused evidence is not used by the cohort sequence fixture.");
    }

    private protected sealed class ManifestThrowingAcceptanceVerifier(Exception exception) : IGoalAcceptanceVerifier
    {
        internal int RunCount { get; private set; }

        public string ComputeEffectivePlanIdentity(
            string worktreePath,
            IReadOnlyList<string>? changedFiles = null) => throw exception;

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            throw new InvalidOperationException("The gate must not run when manifest identity cannot be computed.");
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Focused evidence is not used by the manifest-failure fixture.");
    }

}
