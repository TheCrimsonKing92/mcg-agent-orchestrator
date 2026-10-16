using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: a unique fixture repository per class, and unique index/prompt directories per fact.
// PrepareTask writes a brief only; no worker or test process is started by these facts.
public sealed class ConductorDriverTestsGateFailureLandingAttribution :
    IClassFixture<GateFailureLandingAttributorTests.RepositoryFixture>
{
    private const string Identity = "Ns.RebindReplayProbe.Case";
    private const string Header = "Failing class attribution (candidate diff and main history only; no new test run):";
    private readonly GateFailureLandingAttributorTests.RepositoryFixture _repository;

    public ConductorDriverTestsGateFailureLandingAttribution(GateFailureLandingAttributorTests.RepositoryFixture repository) =>
        _repository = repository;

    [Fact]
    public void RetryRecordsAttributionAndPublishesItInDeveloperBrief()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = VerifiedGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            string? retryMessage = null;
            string? promptPath = null;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(FailingCheck()),
                retryTaskWithCause: (goalId, taskId, message, _, cause) =>
                {
                    Assert.Equal(developer.Id, taskId);
                    Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                    retryMessage = message;
                    var task = kernel.RetryTask(goalId, taskId, message, cause);
                    promptPath = WorkerProfileDispatcher.PrepareTask(
                        kernel, goal, task,
                        new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory} {promptPath}"),
                        Path.Combine(root, "prompts"), _repository.Root, DateTimeOffset.UtcNow,
                        providerName: "OpenAI", modelName: AgentCatalog.OpenAiSubscriptionModelAlias).PromptPath;
                    return task;
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Driver.cs"],
                apparatusRedGate: CreateGate(root));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            var message = Assert.IsType<string>(retryMessage);
            Assert.StartsWith("Acceptance criteria unmet; retrying task with feedback (attempt 1/", message);
            Assert.Contains(Header, message);
            Assert.Contains(InteractionLine(), message);
            Assert.True(message.IndexOf(Header, StringComparison.Ordinal) > message.IndexOf("Concrete acceptance failure evidence", StringComparison.Ordinal));
            Assert.Contains(developer.CriterionRetryFeedback, line => line.Contains(InteractionLine(), StringComparison.Ordinal));
            var prompt = File.ReadAllText(Assert.IsType<string>(promptPath));
            Assert.Contains("## Unmet acceptance criteria from the prior attempt - fix these:", prompt);
            Assert.Contains(InteractionLine(), prompt);
            Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SpentRetryBudgetAppendsAttributionToEscalation()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (_, goal) = VerifiedGoal();
            string? escalation = null;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(FailingCheck()),
                retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Spent budget must not retry."),
                writeEscalation: (_, _, reason) => escalation = reason,
                getLandingFileScopes: _ => ["src/Driver.cs"],
                apparatusRedGate: CreateGate(root));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative with { MaxCriterionRetries = 0 });

            Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            var reason = Assert.IsType<string>(escalation);
            Assert.Contains("Acceptance criteria unmet after", reason);
            Assert.Contains("; " + Header + "; " + InteractionLine(), reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GateWithoutAttributorPreservesRetryMessageWithoutHeader()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = VerifiedGoal();
            string? retryMessage = null;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(FailingCheck()),
                retryTaskWithCause: (goalId, taskId, message, _, cause) =>
                {
                    retryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message, cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Driver.cs"],
                apparatusRedGate: CreateGate(root, includeAttributor: false));

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var message = Assert.IsType<string>(retryMessage);
            Assert.StartsWith("Acceptance criteria unmet; retrying task with feedback (attempt 1/", message);
            Assert.DoesNotContain(Header, message);
            Assert.DoesNotContain("likely interaction", message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private (AgentOrchestratorKernel Kernel, Goal Goal) VerifiedGoal()
    {
        var (kernel, goal) = SoftwareGoal();
        foreach (var task in goal.Tasks)
        {
            DispatchTask(kernel, goal, task, workingDirectory: _repository.Root, baseCommit: _repository.AnchorSha);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "verify", _repository.Root, 0, "ok", "", DateTimeOffset.Parse("2026-10-15T12:00:00Z"),
                HasCommittedChanges: task.RequiredRole == AgentRole.Developer, ReviewedCommit: _repository.AnchorSha));
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
        }
        Assert.Equal(_repository.AnchorSha, GateFailureLandingAttributor.SelectLastPassingCandidateSha(goal));
        return (kernel, goal);
    }

    private ApparatusRedGate CreateGate(string root, bool includeAttributor = true) => new(
        CreateIndex(root), _ => _repository.Root, () => DateTimeOffset.Parse("2026-10-15T12:30:00Z"),
        landingAttributor: includeAttributor ? new GateFailureLandingAttributor(_repository.Root) : null);

    private static AcceptanceFailingTestIndex CreateIndex(string root) =>
        new(Path.Combine(root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));

    private static AcceptanceCheckResult FailingCheck() => new(
        "focused infrastructure tests", false, 1, "Assert.Equal() Failure: expected rebound candidate",
        FailingTestIdentities: [Identity],
        FailingTestAttributions: [new AcceptanceTestFailureAttribution(
            Identity, AcceptanceTestFailureOrigin.Introduced, "focused identity was green at merge-base")]);

    private AcceptanceVerificationSummary RedSummary(params AcceptanceCheckResult[] unmet) => new(
        false, unmet,
        FailedChecks: unmet.Where(check => !check.Passed).Select(check => check.Name).ToArray(),
        BranchHeadSha: _repository.MergeSha,
        MainHeadSha: _repository.MainSha,
        CheckAttributions: unmet.Select(check => new AcceptanceCheckAttribution(
            check.Name, AcceptanceFailureOrigin.Introduced, "main is attested green")).ToArray());

    private string InteractionLine() =>
        $"RebindReplayProbe: added by goal 6a4ec362 (commit {_repository.LandingSha[..9]}) on main after this candidate's last pass at {_repository.AnchorSha[..8]}; likely interaction with that landing.";
}
