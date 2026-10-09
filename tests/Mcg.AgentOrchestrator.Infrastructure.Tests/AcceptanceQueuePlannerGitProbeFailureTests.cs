using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceQueuePlannerGitProbeFailureTests
{
    [Xunit.Theory]
    [Xunit.InlineData("diff", "git diff --name-only HEAD...", "128")]
    [Xunit.InlineData("merge-base", "git merge-base --is-ancestor HEAD", "128")]
    [Xunit.InlineData("status", "git status --porcelain=v1 --untracked-files=all", "128")]
    public void FailedProbeIsBlockedWithGitDiagnostic(string failingProbe, string expectedCommand, string expectedExit)
    {
        WithPlan(failingProbe, new GitCli.GitResult(128, "", "fatal: probe failed\nsecond line"), (item, _) =>
        {
            Assert.Equal(AcceptanceQueueDisposition.Blocked, item.Disposition);
            Assert.Contains(expectedCommand, item.Reason);
            Assert.Contains(expectedExit, item.Reason);
            Assert.Contains("fatal: probe failed", item.Reason);
            Assert.DoesNotContain("second line", item.Reason);
            Assert.DoesNotContain("no diff", item.Reason);
            Assert.DoesNotContain("cannot fast-forward", item.Reason);
            Assert.DoesNotContain("uncommitted changes", item.Reason);
        });
    }

    [Xunit.Fact]
    public void FastForwardExitOneIsHeld()
    {
        WithPlan("merge-base", new GitCli.GitResult(1, "", ""), (item, _) =>
        {
            Assert.Equal(AcceptanceQueueDisposition.Held, item.Disposition);
            Assert.Contains("cannot fast-forward", item.Reason);
        });
    }

    [Xunit.Fact]
    public void FastForwardProcessThatNeverStartedIsBlocked()
    {
        WithPlan("merge-base", new GitCli.GitResult(1, "", "failed to start git process", ProcessStarted: false),
            (item, _) =>
            {
                Assert.Equal(AcceptanceQueueDisposition.Blocked, item.Disposition);
                Assert.Contains("git merge-base --is-ancestor HEAD", item.Reason);
                Assert.Contains("exited 1", item.Reason);
                Assert.Contains("failed to start git process", item.Reason);
            });
    }

    [Xunit.Fact]
    public void DiffExitZeroWithEmptyOutputHasNoDiff()
    {
        WithPlan("diff", new GitCli.GitResult(0, "", ""), (item, _) =>
        {
            Assert.Equal(AcceptanceQueueDisposition.Blocked, item.Disposition);
            Assert.Contains("goal branch has no diff against main", item.Reason);
        });
    }

    [Xunit.Fact]
    public void SuccessfulDirtyStatusKeepsUncommittedChangesVerdict()
    {
        WithPlan("status", new GitCli.GitResult(0, " M changed.txt\n", ""), (item, _) =>
        {
            Assert.Equal(AcceptanceQueueDisposition.Blocked, item.Disposition);
            Assert.Contains("worktree has uncommitted changes", item.Reason);
        });
    }

    [Xunit.Fact]
    public void CleanSuccessfulProbesAreReady()
    {
        WithPlan("", new GitCli.GitResult(0, "", ""), (item, _) =>
            Assert.Equal(AcceptanceQueueDisposition.Ready, item.Disposition));
    }

    private static void WithPlan(
        string overriddenProbe,
        GitCli.GitResult probeResult,
        Action<AcceptanceQueueItem, Goal> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-queue-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Git probe classification", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktree = GoalWorktrees.WorktreePath(root, goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            var branch = GoalWorktrees.BranchName(goal.Id);

            GitCli.GitResult Run(string _, IReadOnlyList<string> args)
            {
                var command = args[0];
                if (command == "for-each-ref" && args.Any(arg => arg.Contains("%(objectname)", StringComparison.Ordinal)))
                    return new GitCli.GitResult(0, $"{branch} {new string('a', 40)}\n", "");
                if (command == overriddenProbe)
                    return probeResult;
                return command switch
                {
                    "rev-parse" => new GitCli.GitResult(0, new string('b', 40), ""),
                    "diff" => new GitCli.GitResult(0, "changed.txt\n", ""),
                    _ => new GitCli.GitResult(0, "", "")
                };
            }

            var item = Assert.Single(AcceptanceQueuePlanner.Build(kernel, root, AutonomyPolicy.SupervisedAuto, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, Run).Items);
            assert(item, goal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
