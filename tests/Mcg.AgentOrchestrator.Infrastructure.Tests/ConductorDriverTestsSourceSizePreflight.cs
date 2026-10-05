using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsSourceSizePreflight
{
    [Xunit.Fact]
    public void ViolatingAuthority_FailsBeforeLeaseOrAcceptanceRunner()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Source size preflight");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        WriteViolatingAuthority(worktree);
        RunGit(worktree, "add", ".");
        RunGit(worktree, "commit", "-m", "violate ratchet");

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());
        var candidate = driver.TryBuildParallelAcceptanceCandidate(
            goal,
            ConductorAutonomyPolicy.Conservative,
            0, out _);
        Assert.NotNull(candidate);
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, ".orchestrator", "test-acceptance-attempts"),
            root,
            runInline: true,
            tryRunPreSlot: driver.RunParallelLandingAcceptancePreSlot);
        var acceptanceRan = false;

        var decision = coordinator.Evaluate(
            candidate!,
            ConductorAutonomyPolicy.Conservative,
            (_, _, _, _, _) =>
            {
                acceptanceRan = true;
                return ConductorParallelAcceptanceRunResult.Accepted(
                    candidate!,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);
            });

        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
        Assert.False(acceptanceRan);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, decision.Attempt.Outcome);
        Assert.Empty(decision.Attempt.LeaseReceipts ?? []);
        var detail = Assert.IsType<string>(decision.Run?.Acceptance?.FailureDetail);
        Assert.Contains("guarded.cs has 3 lines", detail, StringComparison.Ordinal);
        Assert.Contains("recorded ceiling of 2", detail, StringComparison.Ordinal);
        Assert.Contains("Extract behavior to a collaborator", detail, StringComparison.Ordinal);
        Assert.Contains("raise the recorded ceiling", detail, StringComparison.Ordinal);
    }

    private static void WriteViolatingAuthority(string root)
    {
        var authorityPath = Path.Combine(
            root,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
        File.WriteAllText(authorityPath, "new SourceSizeCeiling(\"guarded.cs\", 2)");
        File.WriteAllLines(Path.Combine(root, "guarded.cs"), ["one", "two", "three"]);
    }
}
