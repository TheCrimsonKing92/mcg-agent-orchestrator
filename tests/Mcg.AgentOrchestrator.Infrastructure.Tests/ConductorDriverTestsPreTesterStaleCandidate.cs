using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreTesterStaleCandidate
{
    // Bounds only detect a missing runner-entered/released event; ordering comes from those events.
    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public async Task RewrittenHeadDiscardsStaleRunAndRerunsAtNewHead()
    {
        using var scenario = new Scenario();
        var candidateA = scenario.Head;
        var driver = scenario.Driver();

        var first = await scenario.AdvanceAcrossHeadMove(driver, scenario.RewriteHead);
        var candidateB = scenario.Head;

        Assert.Equal(1, GitCli.Run(scenario.Worktree, "merge-base", "--is-ancestor", candidateA, candidateB).ExitCode);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome);
        Assert.Contains(candidateA[..12], held.Reason, StringComparison.Ordinal);
        Assert.Contains(candidateB[..12], held.Reason, StringComparison.Ordinal);
        Assert.Equal(["focused:" + candidateA], scenario.Order);
        Assert.Empty(scenario.RunMarkers);
        Assert.DoesNotContain(scenario.BriefLines(candidateB), line =>
            line.StartsWith("evidence_index:", StringComparison.Ordinal) &&
            line.Contains("candidate_sha=" + candidateA, StringComparison.Ordinal));
        Assert.Equal(WorkTaskStatus.Assigned, scenario.Tester.Status);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([candidateA, candidateB], scenario.RunCandidates);
        Assert.Equal(["focused:" + candidateA, "focused:" + candidateB, "Tester"], scenario.Order);
        var marker = Assert.Single(scenario.RunMarkers);
        Assert.Contains("outcome=green; candidate_sha=" + candidateB + ";", marker.Message, StringComparison.Ordinal);
        var receipt = PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, candidateB);
        Assert.NotNull(receipt);
        Assert.Equal("green", receipt.Outcome);
        Assert.Contains(scenario.BriefLines(candidateB), line =>
            line.StartsWith("evidence_index:", StringComparison.Ordinal) &&
            line.Contains("candidate_sha=" + candidateB, StringComparison.Ordinal) &&
            line.Contains("receipt=" + receipt.ReceiptId, StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async Task FastForwardedHeadKeepsAncestorCandidateReceipt()
    {
        using var scenario = new Scenario();
        var candidateA = scenario.Head;
        var driver = scenario.Driver();

        var result = await scenario.AdvanceAcrossHeadMove(driver, scenario.FastForwardHead);
        var candidateB = scenario.Head;

        Assert.NotEqual(candidateA, candidateB);
        Assert.Equal(0, GitCli.Run(scenario.Worktree, "merge-base", "--is-ancestor", candidateA, candidateB).ExitCode);
        Assert.IsNotType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(["focused:" + candidateA, "Tester"], scenario.Order);
        Assert.Equal([candidateA], scenario.RunCandidates);
        Assert.Contains("outcome=green; candidate_sha=" + candidateA + ";",
            Assert.Single(scenario.RunMarkers).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cccccccccccccccccccccccccccccccccccccccc")]
    public void UnavailableHeadOrAncestryKeepsCurrentCandidateBehavior(string? unresolvedHead)
    {
        using var scenario = new Scenario();
        var candidate = scenario.Head;
        var driver = scenario.Driver(afterRunnerEntered: () => scenario.ContextHeadOverride = () => unresolvedHead);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(["focused:" + candidate, "Tester"], scenario.Order);
        Assert.Contains("outcome=green; candidate_sha=" + candidate + ";",
            Assert.Single(scenario.RunMarkers).Message, StringComparison.Ordinal);
    }

    private sealed class Scenario : IDisposable
    {
        private const string ProjectPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
        private readonly TaskCompletionSource _runnerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _runnerReleased = new();
        private bool _holdRunner;

        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Tester { get; }
        public string Worktree { get; }
        public List<string> RunCandidates { get; } = [];
        public List<string> Order { get; } = [];
        public Func<string?>? ContextHeadOverride { get; set; }
        public string Head
        {
            get
            {
                var result = GitCli.Run(Worktree, "rev-parse", "HEAD");
                Assert.Equal(0, result.ExitCode);
                return result.Output.Trim();
            }
        }
        public IEnumerable<ProgressEvent> RunMarkers => Goal.Timeline.Where(evt =>
            evt.TaskId == Tester.Id && evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith("finding-evidence pre-tester ", StringComparison.Ordinal));

        public Scenario()
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester stale candidate");
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            var developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            Directory.CreateDirectory(Path.Combine(Worktree, ProjectPath));
            RunGit(Worktree, "init", "--initial-branch=main");
            RunGit(Worktree, "config", "user.email", "test@example.com");
            RunGit(Worktree, "config", "user.name", "Test User");
            File.WriteAllText(Path.Combine(Worktree, ProjectPath, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Commit("baseline");
            var baseSha = Head;
            RunGit(Worktree, "checkout", "-b", "goal");
            WriteDeveloperChange();
            Commit("Developer candidate A");
            Assert.NotEqual(baseSha, Head);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != developer.Id))
                PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, developer, baseCommit: baseSha);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: " + ProjectPath + "/DeferredAlphaTests.cs", "commands: none",
                "tests: deferred - DeferredAlphaTests", "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, developer.Id, new TaskVerificationRecord(
                "test.exe", Worktree, 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
        }

        public ConductorDriver Driver(Action? afterRunnerEntered = null)
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(Root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(
                    ContextHeadOverride is null ? Head : ContextHeadOverride()!),
                focusedEvidenceAttemptCoordinator: coordinator,
                runFocusedEvidence: (_, request) =>
                {
                    // Read the captured candidate handed to this runner from its real attempt record.
                    var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([Goal.Id.Value]));
                    var candidate = attempt.BranchHeadSha!;
                    Assert.Equal(Head, candidate);
                    RunCandidates.Add(candidate);
                    Order.Add("focused:" + candidate);
                    _runnerEntered.TrySetResult();
                    if (_holdRunner && RunCandidates.Count == 1 && !_runnerReleased.Wait(TimeSpan.FromSeconds(30)))
                        throw new Xunit.Sdk.XunitException("Runner release event was not signalled after the head move.");
                    afterRunnerEntered?.Invoke();
                    return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                        Root, request, candidate);
                },
                dispatchAndStart: goal =>
                {
                    Order.Add(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole.ToString());
                    return DispatchStartOutcome.Started();
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(30));
            return driver;
        }

        public async Task<ConductorAdvanceResult> AdvanceAcrossHeadMove(ConductorDriver driver, Action moveHead)
        {
            _holdRunner = true;
            var advance = Task.Run(() => driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative));
            try
            {
                try { await _runnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (TimeoutException)
                {
                    throw new Xunit.Sdk.XunitException("Focused runner entered event was not signalled.");
                }
                moveHead();
            }
            finally
            {
                _runnerReleased.Set();
                await advance;
            }
            return await advance;
        }

        public void RewriteHead()
        {
            RunGit(Worktree, "checkout", "main");
            File.WriteAllText(Path.Combine(Worktree, "main.txt"), "new main commit");
            Commit("new main");
            RunGit(Worktree, "checkout", "-B", "goal", "main");
            WriteDeveloperChange();
            Commit("re-integrated Developer candidate B");
        }

        public void FastForwardHead()
        {
            File.WriteAllText(Path.Combine(Worktree, "descendant.txt"), "descendant of A");
            Commit("descendant candidate B2");
        }

        public IEnumerable<string> BriefLines(string candidate) => Kernel.BuildTaskBriefSource(
            Goal.Id, Tester.Id, targetHeadCommit: candidate).Segments.SelectMany(segment => segment.Lines);

        private void WriteDeveloperChange() => File.WriteAllText(
            Path.Combine(Worktree, ProjectPath, "DeferredAlphaTests.cs"), "public class DeferredAlphaTests {}");

        private void Commit(string message)
        {
            RunGit(Worktree, "add", ".");
            RunGit(Worktree, "commit", "-m", message);
        }

        public void Dispose()
        {
            _runnerReleased.Dispose();
            var elapsed = TimeSpan.Zero;
            SharedTestSupport.RemoveTempDirectory(Root, () => elapsed, wait => elapsed += wait);
        }
    }
}
