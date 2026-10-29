using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its kernel, registry, gate artifacts and locally configured Git repository.
public sealed class LandingHoldSkipReasonTests
{
    private const string RemovedClass = "RemovedFixtureTests";
    private const string GatedClass = ProspectiveEvidenceGateFixture.IncidentClass;
    private static readonly DateTimeOffset DispatchTime = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IncompleteDeveloper_HoldsWithRequestAndSkipReason()
    {
        using var fixture = new Fixture();
        var scenario = fixture.CreateScenario();
        fixture.Kernel.ReportWorkerTaskProgress(scenario.Goal.Id, scenario.Developer.Id,
            WorkTaskStatus.Failed, "Latest round did not complete.");
        var wait = fixture.CreateWait(scenario, "developer");
        fixture.WriteGate(scenario.Goal);

        var result = fixture.Evaluate(scenario.Goal);

        AssertHeld(wait, result.HoldDescription, "owner-round-incomplete");
    }

    [Fact]
    public void MissingGateClass_IndividualAndGroupedHoldsNameReason()
    {
        using var fixture = new Fixture();
        var scenario = fixture.CreateScenario(tests: "MissingGateTests");
        var wait = fixture.CreateWait(scenario, "developer");
        fixture.WriteGate(scenario.Goal);

        var result = fixture.Evaluate(scenario.Goal);
        var grouped = LandingExecutor.DescribeGroupedPendingHumanInput(
            fixture.Kernel, [scenario.Goal], fixture.Workspace, _ => fixture.Candidate);

        AssertHeld(wait, result.HoldDescription, "class-not-in-passed-gate:MissingGateTests");
        AssertHeld(wait, grouped, "class-not-in-passed-gate:MissingGateTests");
    }

    [Theory]
    [InlineData("operator", "candidate")]
    [InlineData("developer", null)]
    public void NoClosureReason_RetainsSubmitHumanInputDetail(string owner, string? candidate)
    {
        using var fixture = new Fixture();
        var scenario = fixture.CreateScenario();
        var wait = fixture.CreateWait(scenario, owner);

        var result = LandingExecutor.EvaluatePendingHumanInput(
            fixture.Kernel, scenario.Goal, fixture.Workspace, candidate);

        Assert.False(wait.IsCompleted);
        Assert.Contains(wait.Id.Value, result.HoldDescription);
        Assert.Contains("; answer required through SubmitHumanInput", result.HoldDescription);
        Assert.DoesNotContain("owner round not accepted as evidence", result.HoldDescription);
    }

    [Fact]
    public void SatisfyingDeveloperReport_ClosesWaitWithoutHold()
    {
        using var fixture = new Fixture();
        var scenario = fixture.CreateScenario();
        var wait = fixture.CreateWait(scenario, "developer");
        var attempt = fixture.WriteGate(scenario.Goal);

        var result = fixture.Evaluate(scenario.Goal);

        Assert.Null(result.HoldDescription);
        Assert.True(wait.IsCompleted);
        Assert.Contains("ROLE_REPORT_EVIDENCE owner=developer", wait.Answer);
        Assert.Contains($"tests=deferred - {GatedClass}", wait.Answer);
        Assert.Contains(attempt, wait.Answer);
    }

    [Fact]
    public void ReviewerPassOnCandidate_ClosesWaitWithoutHold()
    {
        using var fixture = new Fixture();
        var scenario = fixture.CreateScenario();
        // Record after the review so the test exercises landing's role-report closure.
        fixture.CompleteReviewer(scenario);
        var wait = fixture.CreateWait(scenario, "reviewer");

        var result = fixture.Evaluate(scenario.Goal);

        Assert.Null(result.HoldDescription);
        Assert.True(wait.IsCompleted);
        Assert.Contains("ROLE_REPORT_EVIDENCE owner=reviewer", wait.Answer);
        Assert.Contains("verdict=pass", wait.Answer);
    }

    [Fact]
    public void ClosableMemberWaits_GroupedEvaluationReturnsNoHold()
    {
        using var fixture = new Fixture();
        var developerGoal = fixture.CreateScenario();
        var reviewerGoal = fixture.CreateScenario();
        fixture.WriteGate(developerGoal.Goal);
        fixture.WriteGate(reviewerGoal.Goal);
        fixture.CompleteReviewer(reviewerGoal);
        var developerWait = fixture.CreateWait(developerGoal, "developer");
        var reviewerWait = fixture.CreateWait(reviewerGoal, "reviewer");
        Assert.False(developerWait.IsCompleted);
        Assert.False(reviewerWait.IsCompleted);

        var hold = LandingExecutor.DescribeGroupedPendingHumanInput(fixture.Kernel,
            [developerGoal.Goal, reviewerGoal.Goal], fixture.Workspace, _ => fixture.Candidate);

        Assert.Null(hold);
        Assert.True(developerWait.IsCompleted);
        Assert.True(reviewerWait.IsCompleted);
    }

    [Theory]
    [InlineData("deleted", true)]
    [InlineData("undeclared", false)]
    [InlineData("unrelated-declaration", false)]
    [InlineData("retained", false)]
    [InlineData("never-existed", false)]
    [InlineData("comment-only", false)]
    [InlineData("partial-remains", false)]
    [InlineData("ambiguous", false)]
    public void ReportedRemoval_RequiresDeclarationAndDeletedSource(string mode, bool closes)
    {
        using var fixture = new Fixture();
        fixture.InitializeRepository(mode);
        var declaration = mode switch
        {
            "undeclared" => "",
            "unrelated-declaration" => $"OtherTests.{RemovedClass}",
            _ => $"Example.Tests.{RemovedClass}.Check(value: 1)"
        };
        var scenario = fixture.CreateScenario($"{GatedClass}, {RemovedClass}", declaration);
        var wait = fixture.CreateWait(scenario, "developer");
        fixture.WriteGate(scenario.Goal);

        var result = fixture.Evaluate(scenario.Goal);

        if (closes)
        {
            Assert.Null(result.HoldDescription);
            Assert.True(wait.IsCompleted);
            Assert.Contains($"{RemovedClass}@removed-by-goal base {fixture.BaseRevision}", wait.Answer);
            Assert.Contains("Removed.cs", wait.Answer);
        }
        else AssertHeld(wait, result.HoldDescription, $"class-not-in-passed-gate:{RemovedClass}");
    }

    [Fact]
    public void DeclaredRemoval_OtherUngatedClassStillHolds()
    {
        using var fixture = new Fixture();
        fixture.InitializeRepository("deleted");
        var scenario = fixture.CreateScenario($"{RemovedClass}, MissingGateTests", $"{RemovedClass}.Check");
        var wait = fixture.CreateWait(scenario, "developer");
        fixture.WriteGate(scenario.Goal);

        var result = fixture.Evaluate(scenario.Goal);

        AssertHeld(wait, result.HoldDescription, "class-not-in-passed-gate:MissingGateTests");
    }

    private static void AssertHeld(HumanInputRequest wait, string? hold, string reason)
    {
        Assert.False(wait.IsCompleted);
        Assert.NotNull(hold);
        Assert.Contains(wait.Id.Value, hold);
        Assert.Contains($"; owner round not accepted as evidence: {reason}", hold);
    }

    private sealed record Scenario(Goal Goal, TaskSpec Planner, TaskSpec Developer, TaskSpec Reviewer);

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-landing-hold-" + Guid.NewGuid().ToString("N"));
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal OrchestratorWorkspace Workspace { get; }
        internal string Candidate { get; private set; } = new('a', 40);
        internal string? BaseRevision { get; private set; }

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            var registry = new OrchestratorProjectRegistry(Path.Combine(Root, "registry"), Path.Combine(Root, "data"));
            Workspace = OrchestratorWorkspace.ForDirectory(Root, null, null, registry) with { IntegrationBranch = "main" };
        }

        internal Scenario CreateScenario(string tests = GatedClass, string removal = "")
        {
            var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
            var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
            var reviewer = new TaskSpec(TaskId.New(), "Review candidate", AgentRole.Reviewer);
            var criterion = RoleReportEvidenceClosureTests.ReportCriterion;
            var objective = $"Close role reports\n\n## Acceptance criteria\n1. {criterion}" +
                (removal.Length == 0 ? "" : $"\n2. test-removal: `{removal}`");
            var goal = Kernel.CreateGoal(objective, [planner, developer, reviewer]);
            Kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Bind role evidence", [criterion],
                VerificationClass.TestVerifiable, [], []));
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            Kernel.RecordTaskDispatch(goal.Id, developer.Id,
                new TaskDispatchRecord("codex-cli", "implement", Root, DispatchTime, BaseCommit: "base"));
            Kernel.RecordDispatchResultCommit(goal.Id, developer.Id, Candidate);
            Kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord("implement", Root,
                0, RoleReportEvidenceClosureTests.DeveloperOutput($"deferred - {tests}"), string.Empty,
                DispatchTime.AddSeconds(1), WorkerResultPresent: true, HasCommittedChanges: true, DispatchStartedAt: DispatchTime));
            Assert.Equal(WorkTaskStatus.Completed, developer.Status);
            return new(goal, planner, developer, reviewer);
        }

        internal HumanInputRequest CreateWait(Scenario scenario, string owner) =>
            Kernel.RequestHumanInputDeduplicated(scenario.Goal.Id, scenario.Planner.Id, $"Provide {owner} evidence",
                kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
                plannerIdentity: new(scenario.Goal.Id.Value, scenario.Planner.Id.Value,
                    scenario.Goal.AuthoritativeBrief.Version, 1, $"report-{owner}"),
                plannerEvidence: new(1, $"report-{owner}", "post-implementation", owner,
                    "Role report", "Role owns; Acceptance executes", null, null, null)).Request;

        internal string WriteGate(Goal goal) => ProspectiveEvidenceGateFixture.WriteAttempt(
            Workspace.OrchestratorDirectory, goal, Candidate, ProspectiveEvidenceGateFixture.PassingRows).Id;

        internal (ProspectiveEvidenceGateResolution Resolution, string? HoldDescription, IReadOnlyList<string> ObligationLines)
            Evaluate(Goal goal) => LandingExecutor.EvaluatePendingHumanInput(Kernel, goal, Workspace, Candidate);

        internal void CompleteReviewer(Scenario scenario)
        {
            var started = DispatchTime.AddSeconds(2);
            Kernel.RecordTaskDispatch(scenario.Goal.Id, scenario.Reviewer.Id,
                new TaskDispatchRecord("codex-cli", "review", Root, started, BaseCommit: Candidate));
            var output = string.Join(Environment.NewLine, "WORKER_RESULT:", "files: none", "commands: inspect",
                "tests: deferred - GateProofTests", "commit: none", "blockers: none", "findings: []", "touched_anchors: []",
                "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"not-verifiable\",\"evidence\":\"Acceptance executes\"}]",
                "verdict: pass", "model_fit: fixture/model - adequate - inspection", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(scenario.Goal.Id, scenario.Reviewer.Id,
                new TaskVerificationRecord("review", Root, 0, output, string.Empty,
                    started.AddSeconds(1), WorkerResultPresent: true));
            Kernel.ReportWorkerTaskProgress(scenario.Goal.Id, scenario.Reviewer.Id, WorkTaskStatus.Completed, "Review complete.");
            Assert.Equal(Candidate, scenario.Reviewer.LastVerification!.ReviewedCommit);
        }

        internal void InitializeRepository(string mode)
        {
            Git("init", "-b", "main");
            Git("config", "user.name", "Landing hold fixture");
            Git("config", "user.email", "landing-hold@example.invalid");
            Git("config", "commit.gpgsign", "false");
            Git("config", "core.hooksPath", Path.Combine(Root, "no-hooks"));
            File.WriteAllText(Path.Combine(Root, "Gated.cs"), $"public sealed class {GatedClass} {{ }}");
            if (mode != "never-existed")
                File.WriteAllText(Path.Combine(Root, "Removed.cs"), mode == "comment-only"
                    ? $"// public sealed class {RemovedClass} {{ }}"
                    : $"namespace Example.Tests; public partial class {RemovedClass} {{ }}");
            if (mode is "partial-remains" or "ambiguous")
                File.WriteAllText(Path.Combine(Root, "Other.cs"),
                    $"namespace {(mode == "ambiguous" ? "Other.Tests" : "Example.Tests")}; public partial class {RemovedClass} {{ }}");
            Git("add", "--", "*.cs");
            Git("commit", "-m", "Base sources");
            BaseRevision = Git("rev-parse", "HEAD");
            Git("checkout", "-b", "goal-fixture");
            if (mode is not ("retained" or "never-existed")) Git("rm", "--", "Removed.cs");
            if (mode == "ambiguous") Git("rm", "--", "Other.cs");
            Git("commit", "--allow-empty", "-m", "Candidate sources");
            Candidate = Git("rev-parse", "HEAD");
        }

        private string Git(params string[] arguments)
        {
            var result = GitCli.Run(Root, arguments);
            Assert.True(result.Succeeded && !result.DrainTimedOut,
                $"git {string.Join(' ', arguments)} failed: {result.ExitCode} {result.Error}");
            return result.Output.Trim();
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
