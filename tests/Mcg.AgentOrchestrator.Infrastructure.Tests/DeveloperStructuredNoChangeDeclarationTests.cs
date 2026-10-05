using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeveloperStructuredNoChangeDeclarationTests : WorkerDispatchTestSupport
{
    // Synthetic output for boundary coverage; the historical stdout fixture requires
    // a complete c0cfa8ae/2c4a4681 evidence copy (the supplied store reference is truncated).
    private const string Output = """
        WORKER_RESULT:
        files: none
        commands: none
        tests: deferred - GoalAcceptanceVerifierSplitFactParityTests, GoalAcceptanceVerifierSizeRatchetTests, GoalAcceptanceEvidenceBundleTests, GoalAcceptanceVerifierDotnetBuildSlotTests, CanaryEngineSurfaceCoverageTests
        commit: none
        blockers: none
        assigned_scope_complete: true
        model_fit: test/model - adequate - structured declaration fixture
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;
    private static readonly string[] Classes =
    [
        "GoalAcceptanceVerifierSplitFactParityTests", "GoalAcceptanceVerifierSizeRatchetTests",
        "GoalAcceptanceEvidenceBundleTests", "GoalAcceptanceVerifierDotnetBuildSlotTests",
        "CanaryEngineSurfaceCoverageTests"
    ];

    [Xunit.Fact]
    public void Qualify_StructuredRetry_PreservesClassesAndFilesRationale()
    {
        var (goal, task) = QualifierScenario();

        Xunit.Assert.True(Qualify(goal, task, Output, out var outcome, out var code));

        Xunit.Assert.Null(code);
        Xunit.Assert.Equal(Classes, outcome.TestClasses);
        Xunit.Assert.Equal("files: none", outcome.Rationale);
        Xunit.Assert.Equal(task.LastDispatch!.BaseCommit, outcome.CandidateSha);
    }

    [Xunit.Fact]
    public void Refresh_StructuredRetry_RecordsDeferredOutcomeAndClassifier()
    {
        var (kernel, goal, task, runner, candidate) = DispatchScenario(Output);

        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        var declaration = Xunit.Assert.Single(task.LastVerification.StandardError.Split('\n')
            .Where(line => line.StartsWith("DEFERRED_NO_CHANGE_STRUCTURED_DECLARATION", StringComparison.Ordinal)));
        Xunit.Assert.Equal("DEFERRED_NO_CHANGE_STRUCTURED_DECLARATION candidate_sha=" + candidate,
            declaration.TrimEnd('\r'));
        Xunit.Assert.Contains("DEFERRED_NO_CHANGE_OUTCOME", task.LastVerification.StandardError, StringComparison.Ordinal);
        Xunit.Assert.True(DeferredNoChangeOutcome.TryParse(task.LastVerification.StandardError, out var outcome));
        Xunit.Assert.Equal(candidate, outcome.CandidateSha);
        Xunit.Assert.Equal(Classes, outcome.TestClasses);
        Xunit.Assert.Equal("files: none", outcome.Rationale);
        var classifier = goal.Timeline.Last(item => item.TaskId == task.Id &&
            item.Message.StartsWith("CLASSIFIER ", StringComparison.Ordinal));
        Xunit.Assert.Contains("rule=deferred-no-change-round", classifier.Message, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("files: none", "files: Alpha.cs")]
    [Xunit.InlineData("commit: none", "commit: abc1234")]
    [Xunit.InlineData("assigned_scope_complete: true", "assigned_scope_complete: false")]
    [Xunit.InlineData("blockers: none", "blockers: source work remains")]
    [Xunit.InlineData("tests: deferred", "tests: pass")]
    public void Qualify_OneBrokenField_KeepsExistingDeclineAndNoOutcome(string from, string to)
    {
        var output = Output.Replace(from, to, StringComparison.Ordinal);
        var (goal, task) = QualifierScenario();

        Xunit.Assert.False(Qualify(goal, task, output, out _, out var code));
        Xunit.Assert.Equal("no-rationale", code);

        var (kernel, dispatchGoal, dispatchTask, runner, _) = DispatchScenario(output);
        runner.RefreshLatestProcess(kernel, dispatchGoal.Id, dispatchTask.Id);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_OUTCOME", dispatchTask.LastVerification!.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_DECLINED", dispatchTask.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Refresh_UndeclaredFailingClass_RecordsOneDecline()
    {
        var (kernel, goal, task, runner, _) = DispatchScenario(Output,
            "ACTIONABLE_CANDIDATE_RED failing_tests=Suite.UndeclaredTests.Fails");

        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        var decline = Xunit.Assert.Single(task.LastVerification.StandardError.Split('\n')
            .Where(line => line.StartsWith("DEFERRED_NO_CHANGE_DECLINED", StringComparison.Ordinal)));
        Xunit.Assert.Equal("DEFERRED_NO_CHANGE_DECLINED reason=failing-test-class-undeclared", decline.TrimEnd('\r'));
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_OUTCOME", task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Qualify_TrimmedMixedCaseFields_PreservesVerbatimFilesLine()
    {
        var output = Output.Replace("files: none", "  Files:  NoNe  ", StringComparison.Ordinal)
            .Replace("commit: none", "commit:  NONE  ", StringComparison.Ordinal)
            .Replace("assigned_scope_complete: true", "assigned_scope_complete:  TRUE  ", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        var (goal, task) = QualifierScenario();

        Xunit.Assert.True(Qualify(goal, task, output, out var outcome, out _));

        Xunit.Assert.Equal("Files:  NoNe", outcome.Rationale);
        Xunit.Assert.Contains(outcome.Rationale, output, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("files: none", "files: none.")]
    [Xunit.InlineData("files: none", "files: (none)")]
    [Xunit.InlineData("commit: none", "commit: no changes")]
    [Xunit.InlineData("assigned_scope_complete: true", "assigned_scope_complete: true - complete")]
    [Xunit.InlineData("assigned_scope_complete: true", "scope_complete: true")]
    public void Qualify_InexactStructuralToken_Declines(string from, string to)
    {
        var (goal, task) = QualifierScenario();

        Xunit.Assert.False(Qualify(goal, task, Output.Replace(from, to, StringComparison.Ordinal), out _, out var code));

        Xunit.Assert.Equal("no-rationale", code);
    }

    [Xunit.Fact]
    public void Qualify_NoDeclaredClasses_DeclinesWithoutRationale()
    {
        var (goal, task) = QualifierScenario();
        var output = Output.Replace("deferred - " + string.Join(", ", Classes), "deferred", StringComparison.Ordinal);

        Xunit.Assert.False(Qualify(goal, task, output, out _, out var code));

        Xunit.Assert.Equal("no-rationale", code);
    }

    [Xunit.Fact]
    public void Qualify_FinalBlockAndDuplicateField_UsesAuthoritativeFilesLine()
    {
        var (goal, task) = QualifierScenario();
        var output = Output + "\n" + Output.Replace("files: none", "files: Alpha.cs\nFiles:  None", StringComparison.Ordinal)
            + "\nfiles: unrelated prose";

        Xunit.Assert.True(Qualify(goal, task, output, out var outcome, out _));

        Xunit.Assert.Equal("Files:  None", outcome.Rationale);
    }

    [Xunit.Fact]
    public void Refresh_ExplicitRationale_WinsWithoutStructuredDiagnostic()
    {
        const string rationale = "NO_CHANGE: the candidate already contains the repair.";
        var (kernel, goal, task, runner, _) = DispatchScenario(rationale + "\n" + Output);

        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.True(DeferredNoChangeOutcome.TryParse(task.LastVerification.StandardError, out var outcome));
        Xunit.Assert.Equal(rationale, outcome.Rationale);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_STRUCTURED_DECLARATION", task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    private static (Goal Goal, TaskSpec Task) QualifierScenario()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement the retry.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Structured no-change", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTask(goal.Id, task.Id, "Review current candidate.");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", DateTimeOffset.UtcNow, BaseCommit: new string('b', 40)));
        return (goal, task);
    }

    private static bool Qualify(Goal goal, TaskSpec task, string output,
        out DeferredNoChangeOutcome outcome, out string? code)
    {
        var catalog = WorkerProviderCatalog.Default();
        var classifier = new WorkerDispatchCompletionClassifier(
            dispatch => catalog.ResolveProfile(dispatch.WorkerName), new SystemClock(), _ => false,
            _ => throw new FileNotFoundException());
        return DeveloperDeferredNoChangeQualifier.TryQualify(goal, task, task.LastDispatch!.BaseCommit!,
            true, false, output, string.Empty, classifier, out outcome, out code);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task,
        BackgroundDispatchRunner Runner, string Candidate) DispatchScenario(string output,
        string feedback = "Review the unchanged candidate after evidence.")
    {
        var root = CreateSeededDispatchRepository();
        var clock = new MutableClock(DateTimeOffset.UtcNow.AddMinutes(2));
        var commitAt = clock.UtcNow.AddSeconds(1);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer, WorkerResultBlock("seed.txt", "fixture verification",
                "pass - fixture verification completed"), string.Empty, clock,
            mutateWorktree: worktree =>
            {
                File.WriteAllText(Path.Combine(worktree, "seed.txt"), "candidate repair");
                RunGit(worktree, ["add", "seed.txt"], commitAt);
                RunGit(worktree, ["commit", "-m", "Fixture candidate repair"], commitAt);
            });
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single(other => other.Id == task.Id);
        var candidate = ReadGit(process.WorkingDirectory, ["rev-parse", "HEAD"]);
        var parent = ReadGit(process.WorkingDirectory, ["rev-parse", "HEAD^"]);
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, parent);
        var runner = new BackgroundDispatchRunner(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(candidate, task.LastDispatch!.ResultCommit);
        Xunit.Assert.NotEqual(task.LastDispatch.BaseCommit, task.LastDispatch.ResultCommit);

        kernel.RetryTask(goal.Id, task.Id, feedback);
        clock.Advance(TimeSpan.FromSeconds(1));
        var logs = Path.GetDirectoryName(process.StandardOutputPath)!;
        var next = process with
        {
            StandardOutputPath = Path.Combine(logs, "round-2.out.log"),
            StandardErrorPath = Path.Combine(logs, "round-2.err.log"),
            ExitCodePath = Path.Combine(logs, "round-2.exit.txt"),
            StartedAt = clock.UtcNow,
            CompletedAt = null,
            ExitCode = null
        };
        File.WriteAllText(next.StandardOutputPath, output);
        File.WriteAllText(next.StandardErrorPath, string.Empty);
        File.WriteAllText(next.ExitCodePath, "0");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command,
            process.WorkingDirectory, clock.UtcNow, BaseCommit: candidate));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, next);
        clock.Advance(TimeSpan.FromSeconds(1));
        Xunit.Assert.Equal(candidate, task.LastDispatch!.BaseCommit);
        Xunit.Assert.Equal(string.Empty, ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));
        return (kernel, goal, task, runner, candidate);
    }
}
