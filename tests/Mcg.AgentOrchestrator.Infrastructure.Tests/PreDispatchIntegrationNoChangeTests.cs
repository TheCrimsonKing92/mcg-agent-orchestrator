using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PreDispatchIntegrationNoChangeTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void IntegratedDeveloperRetryWithFreshPassingVerificationAllowsNoChangeCompletion()
    {
        var root = CreateSeededDispatchRepository();
        var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-09-07T22:45:42Z")));
        var developer = new TaskSpec(
            TaskId.New(),
            "Integrate main and verify the accepted candidate.",
            AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Verify the integrated candidate.", AgentRole.Tester);
        var goal = kernel.CreateGoal("Conductor integration retry", [developer, tester]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTask(
            goal.Id,
            developer.Id,
            "Integrate current main before re-verifying the candidate.",
            RetryCause.MainDriftConflict);
        var resultingCandidate = ReadGit(root, ["rev-parse", "HEAD"]);
        var integration = new DeveloperBranchIntegrationResult(
            DeveloperBranchIntegrationStatus.Integrated,
            "Conductor integrated main before Developer dispatch.",
            [],
            OriginalCandidateSha: new string('a', 40),
            IntegratedMainSha: new string('b', 40),
            ResultingCandidateSha: resultingCandidate);
        new PreDispatchIntegrationReceiptRecorder(kernel).Record(goal, integration);

        WorkerProfileDispatcher.PrepareTask(
            kernel,
            goal,
            developer,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(root, "prompts"),
            root,
            DateTimeOffset.Parse("2026-09-07T22:46:00Z"),
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSubscriptionModelAlias);
        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, resultingCandidate);
        var output = "NO_CHANGE: conductor already completed the requested main integration.\n" +
            WorkerResultBlock(
                "none",
                ".\\scripts\\Invoke-TestSummary.ps1",
                "pass - 1/1 focused verification completed",
                commit: "none",
                blockers: "none");

        var providers = WorkerProviderCatalog.Default();
        var classifier = new WorkerDispatchCompletionClassifier(
            dispatch => providers.ResolveProfile(dispatch.WorkerName),
            new TestClock(DateTimeOffset.Parse("2026-09-07T22:47:00Z")),
            File.Exists,
            File.ReadAllText);

        Xunit.Assert.True(classifier.AllowsNoChangeCompletion(developer, output, string.Empty));
        var receipt = Xunit.Assert.IsType<PreDispatchIntegrationReceipt>(
            developer.LastDispatch!.PreDispatchIntegrationReceipt);
        Xunit.Assert.Equal(goal.Id, developer.LastDispatch.GoalId);
        Xunit.Assert.Equal(resultingCandidate, receipt.ResultingCandidateSha);

        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var stdout = Path.Combine(logs, "Developer.out.log");
        var stderr = Path.Combine(logs, "Developer.err.log");
        var exit = Path.Combine(logs, "Developer.exit.txt");
        File.WriteAllText(stdout, output);
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var process = new TaskProcessRecord(
            999999,
            developer.LastDispatch.Command,
            root,
            stdout,
            stderr,
            exit,
            DateTimeOffset.Parse("2026-09-07T22:46:00Z"),
            null,
            null);
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id, process);
        WriteHeartbeat(
            process,
            DateTimeOffset.Parse("2026-09-07T22:46:00Z"),
            DateTimeOffset.Parse("2026-09-07T22:48:00Z"),
            "completed",
            new FileInfo(stdout).Length,
            0,
            childPid: null,
            exitFileExists: true);

        new BackgroundDispatchRunner(
            new TestClock(DateTimeOffset.Parse("2026-09-07T22:48:00Z")),
            isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, developer.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Xunit.Assert.Equal(0, developer.LastVerification!.ExitCode);
        Xunit.Assert.DoesNotContain(
            "required relevant file-change evidence",
            developer.LastVerification.StandardError,
            StringComparison.Ordinal);
        var outcome = DispatchFailureClassifier.Classify(developer, developer.LastVerification);
        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Contains("pre-dispatch integration satisfied retry", outcome.EvidenceSummary, StringComparison.Ordinal);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredDispatch = restored.GetGoal(goal.Id).Tasks.Single(task => task.Id == developer.Id).LastDispatch!;
        Xunit.Assert.Equal(goal.Id, restoredDispatch.GoalId);
        Xunit.Assert.Equal(receipt, restoredDispatch.PreDispatchIntegrationReceipt);
    }

    [Xunit.Fact]
    public void IntegrationReceiptRejectsForeignStaleAndCandidateDriftBindings()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-07T22:45:42Z"));
        var kernel = new AgentOrchestratorKernel(clock);
        var developer = new TaskSpec(TaskId.New(), "Verify the integrated candidate.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Bind integration evidence", [developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTask(goal.Id, developer.Id, "Integrate main.", RetryCause.MainDriftConflict);
        var candidate = new string('c', 40);
        var receipt = new PreDispatchIntegrationReceipt(
            goal.Id,
            developer.Id,
            developer.LatestRetryAt!.Value,
            new string('a', 40),
            new string('b', 40),
            candidate);

        Xunit.Assert.True(receipt.Matches(goal.Id, developer, candidate));
        Xunit.Assert.False((receipt with { GoalId = GoalId.New() }).Matches(goal.Id, developer, candidate));
        Xunit.Assert.False((receipt with { TaskId = TaskId.New() }).Matches(goal.Id, developer, candidate));
        Xunit.Assert.False((receipt with { RetryAt = receipt.RetryAt.AddTicks(-1) }).Matches(goal.Id, developer, candidate));
        Xunit.Assert.False(receipt.Matches(goal.Id, developer, new string('d', 40)));
    }

    [Xunit.Fact]
    public void IntegrationReceiptProducerRequiresCurrentMainDriftIntentAndValidIdentities()
    {
        var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-09-07T22:45:42Z")));
        var developer = new TaskSpec(TaskId.New(), "Resolve a source finding.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Keep unresolved implementation blocked", [developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTask(goal.Id, developer.Id, "Fix the unresolved implementation.", RetryCause.NewSourceFinding);
        var recorder = new PreDispatchIntegrationReceiptRecorder(kernel);
        var integration = new DeveloperBranchIntegrationResult(
            DeveloperBranchIntegrationStatus.Integrated,
            "Integrated main.",
            [],
            new string('a', 40),
            new string('b', 40),
            new string('c', 40));

        recorder.Record(goal, integration);

        Xunit.Assert.Null(developer.PendingPreDispatchIntegrationReceipt);

        kernel.RetryTask(goal.Id, developer.Id, "Integrate main.", RetryCause.MainDriftConflict);
        recorder.Record(goal, new DeveloperBranchIntegrationResult(
            DeveloperBranchIntegrationStatus.Current,
            "Main was already present before this retry.",
            []));
        Xunit.Assert.Null(developer.PendingPreDispatchIntegrationReceipt);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => recorder.Record(
            goal,
            integration with { ResultingCandidateSha = "not-a-commit" }));
        Xunit.Assert.Contains("must expose", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("not-run - no verification executed", "none")]
    [Xunit.InlineData("pass - 0/0 tests executed", "none")]
    [Xunit.InlineData("pass - executed=0 passed=1", "none")]
    [Xunit.InlineData("fail - 1 failed", "none")]
    [Xunit.InlineData("pass - total=1 failed=1", "none")]
    [Xunit.InlineData("inconclusive - runner produced no receipt", "none")]
    [Xunit.InlineData("pass - 1/1", "unresolved implementation remains")]
    public void IntegratedRetryNoChangeRejectsInsufficientVerificationOrBlocker(
        string tests,
        string blockers)
    {
        var (developer, classifier) = PrepareIntegratedRetry();
        var output = "NO_CHANGE: conductor integrated main.\n" +
            WorkerResultBlock(
                "none",
                ".\\scripts\\Invoke-TestSummary.ps1",
                tests,
                commit: "none",
                blockers: blockers);

        Xunit.Assert.False(classifier.AllowsNoChangeCompletion(developer, output, string.Empty));
    }

    private static (TaskSpec Developer, WorkerDispatchCompletionClassifier Classifier) PrepareIntegratedRetry()
    {
        var root = CreateSeededDispatchRepository();
        var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-09-07T22:45:42Z")));
        var developer = new TaskSpec(TaskId.New(), "Verify integrated main.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Integrated retry controls", [developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTask(goal.Id, developer.Id, "Integrate current main.", RetryCause.MainDriftConflict);
        var candidate = ReadGit(root, ["rev-parse", "HEAD"]);
        new PreDispatchIntegrationReceiptRecorder(kernel).Record(
            goal,
            new DeveloperBranchIntegrationResult(
                DeveloperBranchIntegrationStatus.Integrated,
                "Integrated main.",
                [],
                new string('a', 40),
                new string('b', 40),
                candidate));
        WorkerProfileDispatcher.PrepareTask(
            kernel,
            goal,
            developer,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(root, "prompts"),
            root,
            DateTimeOffset.Parse("2026-09-07T22:46:00Z"),
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSubscriptionModelAlias);
        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, candidate);
        var providers = WorkerProviderCatalog.Default();
        return (developer, new WorkerDispatchCompletionClassifier(
            dispatch => providers.ResolveProfile(dispatch.WorkerName),
            new TestClock(DateTimeOffset.Parse("2026-09-07T22:47:00Z")),
            File.Exists,
            File.ReadAllText));
    }
}
