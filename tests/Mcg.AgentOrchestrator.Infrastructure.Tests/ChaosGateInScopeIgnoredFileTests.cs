using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateInScopeIgnoredFileTests : ChaosGateTestBase, IDisposable
{
    private readonly List<string> _roots = [];

    [Xunit.Fact]
    public void Refresh_RecentIgnoredScopeFile_FailsWithTypedDeveloperRetry()
    {
        var (kernel, goal, task) = CreateDispatch(InScopeIgnoredFileCheckTests.NewFile);
        new BackgroundDispatchRunner(isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        var verification = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Assert.Equal(1, verification.ExitCode);
        Assert.Contains(InScopeIgnoredFileCheckTests.NewFile, verification.StandardError);
        Assert.Contains(".gitignore:1:schemadrift", verification.StandardError);
        var outcome = DispatchFailureClassifier.Classify(task, verification);
        Assert.Equal("in-scope-file-ignored", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Contains(InScopeIgnoredFileCheckTests.NewFile, outcome.EvidenceSummary);
    }

    [Xunit.Fact]
    public void Refresh_OutOfScopeIgnoredFile_EqualsCleanBaseline()
    {
        var (baselineKernel, baselineGoal, baselineTask) = CreateDispatch(null);
        var (kernel, goal, task) = CreateDispatch("cmd/schemadrift/notes_test.go");
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        runner.RefreshLatestProcess(baselineKernel, baselineGoal.Id, baselineTask.Id);
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, baselineTask.Status);
        Assert.Equal(0, baselineTask.LastVerification!.ExitCode);
        Assert.Equal(baselineTask.Status, task.Status);
        Assert.Equal(baselineTask.LastVerification.ExitCode, task.LastVerification!.ExitCode);
        Assert.DoesNotContain("in-scope-file-ignored", task.LastVerification.StandardError);
    }

    private (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) CreateDispatch(string? ignoredFile)
    {
        var root = CreateSeededRepo();
        _roots.Add(root);
        var main = Path.Combine(root, "cmd", "schemadrift", "main.go");
        Directory.CreateDirectory(Path.GetDirectoryName(main)!);
        File.WriteAllText(main, "package main");
        RunGit(root, ["add", "--", "cmd/schemadrift/main.go"], DispatchedAt.AddMinutes(-3));
        RunGit(root, ["commit", "-m", "Track main"], DispatchedAt.AddMinutes(-3));
        File.WriteAllText(Path.Combine(root, ".gitignore"), "schemadrift\n");
        RunGit(root, ["add", "--", ".gitignore"], DispatchedAt.AddMinutes(-2));
        RunGit(root, ["commit", "-m", "Ignore schemadrift"], DispatchedAt.AddMinutes(-2));

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(InScopeIgnoredFileCheckTests.Objective,
            [new TaskSpec(TaskId.New(), "Reject invalid delimiters.", AgentRole.Developer)]);
        var agent = new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        CommitSourceFile(worktree, "src/Feature.cs", "// feature");
        var commit = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);
        File.WriteAllText(Path.Combine(worktree, "WORKER_RESULT.md"),
            WorkerResultBlock("src/Feature.cs", "none", "pass", commit: commit));
        RunGit(worktree, ["add", "-A"], CommittedAt.AddSeconds(5));
        RunGit(worktree, ["commit", "-m", "Add WORKER_RESULT.md"], CommittedAt.AddSeconds(5));
        if (ignoredFile is not null)
        {
            File.WriteAllText(Path.Combine(worktree, ignoredFile), "package main");
            Assert.Contains(ignoredFile, ReadGit(worktree, ["ls-files", "--others", "--ignored", "--exclude-standard"]));
        }
        Assert.Empty(ReadGit(worktree, ["status", "--porcelain"]));

        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var stdout = Path.Combine(logs, "developer.out.log");
        var stderr = Path.Combine(logs, "developer.err.log");
        var exit = Path.Combine(logs, "developer.exit.txt");
        File.WriteAllText(stdout, "Work complete. See WORKER_RESULT.md for result details.");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree, DispatchedAt));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(999999, "codex exec prompt", worktree, stdout, stderr, exit, DispatchedAt, null, null));
        return (kernel, goal, task);
    }

    public void Dispose()
    {
        foreach (var root in _roots) SharedTestSupport.RemoveTempDirectory(root);
    }
}
