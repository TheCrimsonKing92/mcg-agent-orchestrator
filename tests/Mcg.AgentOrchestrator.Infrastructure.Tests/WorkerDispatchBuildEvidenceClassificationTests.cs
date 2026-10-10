using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

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
        var scriptPath = OrchestratorBuildEvidenceCheck.ResolveScriptPath(_ => repositoryRoot);
        var projects = new[] { "src/Feature/Feature.csproj", "tests/Feature.Tests/Feature.Tests.csproj" };

        var command = OrchestratorBuildEvidenceCheck.BuildCommand(scriptPath, projects);

        Xunit.Assert.NotNull(scriptPath);
        Xunit.Assert.True(Path.IsPathFullyQualified(scriptPath));
        Xunit.Assert.True(File.Exists(scriptPath));
        Xunit.Assert.Equal($"& '{scriptPath.Replace("'", "''")}' '{projects[0]}' '{projects[1]}'", command);
        Xunit.Assert.Equal(
            OrchestratorBuildEvidenceCheck.BuildCommand(OrchestratorBuildEvidenceCheck.ResolveScriptPath(), projects),
            OrchestratorBuildEvidenceCheck.BuildCommand(projects));
    }

    [Xunit.Fact]
    public void HomeEnvironmentVariableMatchesOrchestratorHomeContract()
    {
        Xunit.Assert.Equal(OrchestratorHome.EnvironmentVariable, OrchestratorBuildEvidenceCheck.HomeEnvironmentVariable);
    }

    [Xunit.Fact]
    public void FormatScriptInvocationQuotesRootedHomePath()
    {
        var home = Path.Combine(CreateTempDirectory(), "o'home");
        var scriptPath = OrchestratorBuildEvidenceCheck.ResolveScriptPath(name =>
        {
            Xunit.Assert.Equal(OrchestratorHome.EnvironmentVariable, name);
            return " " + home + " ";
        });
        var expectedPath = Path.Combine(home, "scripts", "Invoke-WorkerBuildCheck.ps1");

        Xunit.Assert.Equal(expectedPath, scriptPath);
        Xunit.Assert.True(Path.IsPathFullyQualified(scriptPath!));
        Xunit.Assert.Equal($"& '{expectedPath.Replace("'", "''")}'", OrchestratorBuildEvidenceCheck.FormatScriptInvocation(scriptPath));
        Xunit.Assert.Equal(
            $"& '{expectedPath.Replace("'", "''")}' 'src/o''project/Feature.csproj'",
            OrchestratorBuildEvidenceCheck.BuildCommand(scriptPath, ["src/o'project/Feature.csproj"]));
    }

    [Xunit.Fact]
    public void ScriptsLessWorktreeTargetsHomeScriptAndKeepsWorktreeCwd()
    {
        var worktree = CreateTempDirectory();
        AddCompiledFeature(worktree);
        Xunit.Assert.False(Directory.Exists(Path.Combine(worktree, "scripts")));
        var home = InfrastructureTestSupport.FindRepositoryRoot();
        var scriptPath = OrchestratorBuildEvidenceCheck.ResolveScriptPath(_ => home);
        Xunit.Assert.True(File.Exists(scriptPath));
        WorkerProcessRunRequest? launched = null;
        OrchestratorBuildCheckResult? runResult = null;

        var resolution = OrchestratorBuildEvidenceCheck.Resolve(
            worktree, AgentRole.Developer, ["src/Feature/Feature.cs"], [],
            failedWorkerBuildCheck: false,
            workerBuildReceipt: () => new(false, "receipt-missing"),
            request => runResult = OrchestratorBuildEvidenceCheck.Run(request, _ => home, candidate =>
            {
                launched = candidate;
                return new(0, "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=1", string.Empty);
            }));

        // Reverting the path base to WorktreeRoot skips this launcher and produces MissingEvidence.
        Xunit.Assert.NotNull(runResult);
        Xunit.Assert.True(runResult.Ran, runResult.Output);
        Xunit.Assert.NotNull(launched);
        Xunit.Assert.Equal(worktree, launched.WorkingDirectory);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(7), launched.Timeout);
        Xunit.Assert.Equal(
            $"& '{scriptPath!.Replace("'", "''")}' 'src/Feature/Feature.csproj'", launched.Command);
        Xunit.Assert.False(resolution.MissingEvidence, resolution.Diagnostic);
        Xunit.Assert.False(resolution.FailsRound, resolution.Diagnostic);
    }

    [Xunit.Fact]
    public void OwnRepositoryRootResolvesCheckedInScript()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var scriptPath = OrchestratorBuildEvidenceCheck.ResolveScriptPath(_ => root);
        Xunit.Assert.True(File.Exists(scriptPath));
        WorkerProcessRunRequest? launched = null;
        var request = new OrchestratorBuildCheckRequest(root, ["src/Feature/Feature.csproj"], TimeSpan.FromMinutes(1));

        var result = OrchestratorBuildEvidenceCheck.Run(request, _ => root, candidate =>
        {
            launched = candidate;
            return new(0, "PASS build: 0 errors", string.Empty);
        });

        Xunit.Assert.True(result.Ran, result.Output);
        Xunit.Assert.NotNull(launched);
        Xunit.Assert.Equal(root, launched.WorkingDirectory);
        Xunit.Assert.Equal(OrchestratorBuildEvidenceCheck.BuildCommand(scriptPath, request.Projects), launched.Command);
    }

    [Xunit.Fact]
    public void MissingHomeScriptReportsResolvedAbsolutePath()
    {
        var home = CreateTempDirectory();
        var worktree = InfrastructureTestSupport.FindRepositoryRoot();
        Xunit.Assert.True(File.Exists(Path.Combine(worktree, OrchestratorBuildEvidenceCheck.ScriptRelativePath)));
        var scriptPath = OrchestratorBuildEvidenceCheck.ResolveScriptPath(_ => home);
        Xunit.Assert.False(File.Exists(scriptPath));

        var result = OrchestratorBuildEvidenceCheck.Run(
            new(worktree, ["src/Feature/Feature.csproj"], TimeSpan.FromMinutes(1)), _ => home,
            _ => throw new InvalidOperationException("Missing home script must not launch the worktree copy."));

        Xunit.Assert.False(result.Ran);
        Xunit.Assert.Equal(-1, result.ExitCode);
        Xunit.Assert.Equal($"Build evidence script not found: {scriptPath}", result.Output);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData("  ")]
    public void UnresolvedHomeReportsNotFoundWithoutLaunching(string? home)
    {
        Xunit.Assert.Null(OrchestratorBuildEvidenceCheck.ResolveScriptPath(_ => home));
        var result = OrchestratorBuildEvidenceCheck.Run(
            new(InfrastructureTestSupport.FindRepositoryRoot(), ["src/Feature/Feature.csproj"], TimeSpan.FromMinutes(1)),
            _ => home,
            _ => throw new InvalidOperationException("Unresolved home must not launch a build."));

        Xunit.Assert.False(result.Ran);
        Xunit.Assert.Equal(-1, result.ExitCode);
        Xunit.Assert.Equal(
            $"Build evidence script not found: orchestrator home unresolved ({OrchestratorHome.EnvironmentVariable} unset)",
            result.Output);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void DotnetBriefSitesNameSharedResolverInvocation(AgentRole role)
    {
        var worktree = CreateTempDirectory();
        AddCompiledFeature(worktree);
        Xunit.Assert.False(Directory.Exists(Path.Combine(worktree, "scripts")));
        var task = new TaskSpec(TaskId.New(), "Implement the .NET change.", role);
        var goal = new AgentOrchestratorKernel().CreateGoal("Build from orchestrator home", [task]);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, worktree);
        var invocation = OrchestratorBuildEvidenceCheck.FormatScriptInvocation(OrchestratorBuildEvidenceCheck.ResolveScriptPath());

        foreach (var artifact in new[] { "current-task.md", "deterministic-verification.md" })
        {
            var text = File.ReadAllText(Path.Combine(contextDirectory, artifact));
            Xunit.Assert.Contains(invocation + " <project.csproj> [project.csproj...]", text, StringComparison.Ordinal);
            Xunit.Assert.Contains($"Compiling every changed project is required through `{invocation}`", text, StringComparison.Ordinal);
            Xunit.Assert.Contains("from your worktree root", text, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain(@".\scripts\Invoke-WorkerBuildCheck.ps1", text, StringComparison.Ordinal);
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
    public void HelperWithoutBuildVerdict_RejectsUnexpectedBuildRequest()
    {
        var (task, _) = RefreshDeveloper(
            "deferred - acceptance gate owns tests", AddCompiledFeature);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Contains("Unexpected build request: inject the test's build verdict.",
            task.LastVerification!.StandardError, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing),
            task.LastVerification.StandardError, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(
            DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed,
            task.LastVerification.StandardError, StringComparison.Ordinal);
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
            runOrchestratorBuildCheck: runOrchestratorBuildCheck ?? (_ =>
                throw new InvalidOperationException("Unexpected build request: inject the test's build verdict.")))
            .RefreshLatestProcess(kernel, goal.Id, task.Id);
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
