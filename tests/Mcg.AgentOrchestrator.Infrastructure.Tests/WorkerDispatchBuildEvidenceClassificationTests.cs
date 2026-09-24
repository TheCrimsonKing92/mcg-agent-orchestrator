using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerDispatchBuildEvidenceClassificationTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void MissingEvidencePassingCheckCompletesAndCommits(AgentRole role)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root,
            role,
            WorkerResultBlock("src/Feature/Feature.cs", "implemented feature", "deferred - acceptance gate owns tests"),
            string.Empty,
            clock,
            AddCompiledFeature);
        OrchestratorBuildCheckRequest? request = null;

        new BackgroundDispatchRunner(
            clock,
            runOrchestratorBuildCheck: candidate =>
            {
                request = candidate;
                return new(true, 0, "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=1");
            }).RefreshLatestProcess(kernel, goal.Id, task.Id);

        AssertCompleted(task);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.NotNull(request);
        Xunit.Assert.Equal(["src/Feature/Feature.csproj"], request.Projects);
        Xunit.Assert.True(request.Timeout > TimeSpan.FromMinutes(5));
        Xunit.Assert.Contains("build_evidence_producer=orchestrator", task.LastVerification.StandardError, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(
            DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing,
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.Equal(string.Empty, ReadGit(process.WorkingDirectory, ["status", "--short"]));
    }

    [Xunit.Fact]
    public void CompletedRoundRefreshDoesNotRerunOrchestratorBuildCheck()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
            root,
            AgentRole.Developer,
            WorkerResultBlock("src/Feature/Feature.cs", "implemented feature", "deferred - acceptance gate owns tests"),
            string.Empty,
            clock,
            AddCompiledFeature);
        var buildChecks = 0;
        var runner = new BackgroundDispatchRunner(
            clock,
            runOrchestratorBuildCheck: _ =>
            {
                buildChecks++;
                return new(true, 0, "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=1");
            });

        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        AssertCompleted(task);
        Xunit.Assert.Equal(1, buildChecks);
    }

    [Xunit.Fact]
    public void MissingEvidenceCompilerFailurePreservesError()
    {
        var (task, worktree) = RefreshDeveloper(
            "deferred - acceptance gate owns tests",
            AddCompiledFeature,
            _ => new(
                true,
                1,
                "project: src/Feature/Feature.csproj\n" +
                "Feature.cs(1,1): error CS0103: The name 'Missing' does not exist in the current context"));

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        Xunit.Assert.Contains("error CS0103", task.LastVerification.StandardError, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed),
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(
            DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing,
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.NotEqual(string.Empty, ReadGit(worktree, ["status", "--short"]));
    }

    [Xunit.Fact]
    public void MissingEvidenceApparatusFailureKeepsRule()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
            root,
            AgentRole.Developer,
            WorkerResultBlock("src/Feature/Feature.cs", "implemented feature", "deferred - acceptance gate owns tests"),
            string.Empty,
            clock,
            AddCompiledFeature);
        var acceptanceRetriesBefore = goal.AutomaticAcceptanceRetryCount;
        var reviewRoundBefore = ReviewRetryCapReceipt.Create(goal, 2).Round;

        new BackgroundDispatchRunner(
            clock,
            runOrchestratorBuildCheck: _ => new(false, -1, "Build evidence check timed out."))
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Contains(
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing),
            task.LastVerification!.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(
            DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed,
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(
            "build_evidence_producer=orchestrator",
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification);
        Xunit.Assert.Equal(TaskOutcomeClass.UnknownEra, outcome.OutcomeClass);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=worker-build-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Equal(acceptanceRetriesBefore, goal.AutomaticAcceptanceRetryCount);
        Xunit.Assert.Equal(reviewRoundBefore, ReviewRetryCapReceipt.Create(goal, 2).Round);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=1", true)]
    [Xunit.InlineData(0, "build completed", false)]
    [Xunit.InlineData(1, "FAIL build: missing project(s)", false)]
    [Xunit.InlineData(1, "PowerShell failed", false)]
    [Xunit.InlineData(1, "FAIL build: 1 error(s)", true)]
    [Xunit.InlineData(1, "Feature.cs(1,1): error CS0103: Missing", true)]
    public void BuildOutputClassificationRequiresACompileVerdict(int exitCode, string output, bool expectedRan)
    {
        var result = OrchestratorBuildEvidenceCheck.ClassifyOutput(exitCode, output);

        Xunit.Assert.Equal(expectedRan, result.Ran);
        Xunit.Assert.Equal(exitCode, result.ExitCode);
    }

    [Xunit.Fact]
    public void OrchestratorBuildDiagnosticIsBoundedAndRetainsCompilerErrorTail()
    {
        var root = CreateSeededDispatchRepository();
        AddCompiledFeature(root);
        var compilerError = "Feature.cs(1,1): error CS0103: Missing";

        var result = OrchestratorBuildEvidenceCheck.Resolve(
            root,
            AgentRole.Developer,
            ["src/Feature/Feature.cs"],
            [],
            failedWorkerBuildCheck: false,
            workerBuildReceipt: () => new(false, "receipt-missing"),
            _ => new(true, 1, new string('x', VerificationTextBounds.MaxRetainedChars * 2) + "\n" + compilerError));

        Xunit.Assert.True(result.Diagnostic.Length < VerificationTextBounds.MaxRetainedChars + 500);
        Xunit.Assert.Contains(compilerError, result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void BuildCommandUsesSanctionedScriptAndProjects()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var projects = new[] { "src/Feature/Feature.csproj", "tests/Feature.Tests/Feature.Tests.csproj" };

        var command = OrchestratorBuildEvidenceCheck.BuildCommand(projects);

        Xunit.Assert.True(File.Exists(Path.Combine(repositoryRoot, OrchestratorBuildEvidenceCheck.ScriptRelativePath)));
        Xunit.Assert.Contains("./scripts/Invoke-WorkerBuildCheck.ps1", command, StringComparison.Ordinal);
        foreach (var project in projects)
        {
            Xunit.Assert.Contains($"'{project}'", command, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void CompiledChangeWithBuildEvidenceCompletesAndCommits()
    {
        var buildChecks = 0;
        var (task, worktree) = RefreshDeveloper(
            "deferred - Invoke-WorkerBuildCheck passed with 0 errors; acceptance gate owns tests",
            AddCompiledFeature,
            _ => { buildChecks++; return new(true, 0, "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=1"); });

        AssertCompleted(task);
        Xunit.Assert.Equal(1, buildChecks);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    }

    [Xunit.Fact]
    public void NonCompiledChangeWithoutBuildEvidenceKeepsExistingCompletion()
    {
        var (task, worktree) = RefreshDeveloper(
            "deferred - acceptance gate owns tests",
            worktree => File.WriteAllText(Path.Combine(worktree, "README.md"), "documentation"));

        AssertCompleted(task);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    }

    [Xunit.Fact]
    public void ExplicitBuildFailureKeepsExistingTypedReason()
    {
        var (task, _) = RefreshDeveloper(
            "fail - build: 1 error (Invoke-WorkerBuildCheck)",
            AddCompiledFeature);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Contains(
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed),
            task.LastVerification!.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(
            DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing,
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ReviewerDeferredResultIsUnaffected()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var reviewerResult = WorkerResultBlock(
            "none",
            "reviewed candidate",
            "deferred - acceptance gate owns tests")
            .Replace(
                "END_WORKER_RESULT",
                "findings: []\n" +
                "touched_anchors: []\n" +
                "criteria_verdicts: []\n" +
                "verdict: pass\n" +
                "END_WORKER_RESULT",
                StringComparison.Ordinal);
        var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
            root,
            AgentRole.Reviewer,
            reviewerResult,
            string.Empty,
            clock);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        AssertCompleted(task);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
    }

    // A bare status mismatch discards the classifier's reason; every gate failure of this class so far
    // reported only "Expected: Completed / Actual: Failed". Carry the verification receipt so the next
    // failure names its diagnostic marker (worktree-inspection-failed, required-file-change-evidence-missing, ...).
    private static void AssertCompleted(TaskSpec task)
    {
        if (task.Status == WorkTaskStatus.Completed)
        {
            return;
        }

        var verification = task.LastVerification;
        Xunit.Assert.Fail(
            $"Expected task status Completed but found {task.Status}. " +
            $"exit={(verification is null ? "none" : verification.ExitCode.ToString())}; " +
            $"stderr={Tail(verification?.StandardError)}; stdout={Tail(verification?.StandardOutput)}");
    }

    private static string Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        const int limit = 1200;
        return normalized.Length <= limit ? normalized : "..." + normalized[^limit..];
    }

    private static (TaskSpec Task, string Worktree) RefreshDeveloper(
        string tests,
        Action<string> mutateWorktree,
        Func<OrchestratorBuildCheckRequest, OrchestratorBuildCheckResult>? runOrchestratorBuildCheck = null)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root,
            AgentRole.Developer,
            WorkerResultBlock("src/Feature/Feature.cs", "implemented feature", tests),
            string.Empty,
            clock,
            mutateWorktree);

        new BackgroundDispatchRunner(
            clock,
            runOrchestratorBuildCheck: runOrchestratorBuildCheck).RefreshLatestProcess(kernel, goal.Id, task.Id);
        return (task, process.WorkingDirectory);
    }

    private static void AddCompiledFeature(string worktree)
    {
        var directory = Path.Combine(worktree, "src", "Feature");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Feature.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(directory, "Feature.cs"), "public sealed class Feature { }");
    }
}
