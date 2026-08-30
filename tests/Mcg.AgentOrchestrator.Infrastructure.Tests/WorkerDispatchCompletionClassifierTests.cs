using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerDispatchCompletionClassifierTests
{
    [Xunit.Fact]
    public void SuccessfulWorkerResultFromStdoutDoesNotReadArtifacts()
    {
        var classifier = CreateClassifier(
            fileExists: _ => throw new InvalidOperationException("stdout classification must not touch artifacts"),
            readArtifactText: _ => throw new InvalidOperationException("stdout classification must not read artifacts"));

        var classified = classifier.HasSuccessfulWorkerResult(
            "unused",
            WorkerResultBlock("src/Feature.cs", "dotnet test", "pass - 1/1"),
            string.Empty);

        Xunit.Assert.True(classified);
    }

    [Xunit.Fact]
    public void SuccessfulWorkerResultFallsBackToInjectedArtifactReader()
    {
        var path = Path.Combine("memory-worktree", "WORKER_RESULT.md");
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [path] = WorkerResultBlock("src/Feature.cs", "dotnet test", "pass - 1/1")
        };
        var classifier = CreateClassifier(artifacts.ContainsKey, artifactPath => artifacts[artifactPath]);

        var classified = classifier.HasSuccessfulWorkerResult("memory-worktree", string.Empty, string.Empty);

        Xunit.Assert.True(classified);
    }

    [Xunit.Fact]
    public void WorkerResultArtifactPresenceUsesStdoutThenInjectedArtifacts()
    {
        var artifactPath = Path.Combine("memory-worktree", "WORKER_RESULT.txt");
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [artifactPath] = WorkerResultBlock("none", "none", "deferred - acceptance gate")
        };
        var classifier = CreateClassifier(artifacts.ContainsKey, path => artifacts[path]);

        Xunit.Assert.True(classifier.HasWorkerResultArtifact("unused", WorkerResultBlock("none", "none", "not-run - inspection")));
        Xunit.Assert.True(classifier.HasWorkerResultArtifact("memory-worktree", string.Empty));
        Xunit.Assert.False(classifier.HasWorkerResultArtifact("missing-worktree", string.Empty));
    }

    [Xunit.Fact]
    public void NoChangeCompletionIsAllowedOnlyForNonDeveloperWithExplicitRationale()
    {
        var classifier = CreateClassifier();
        var planner = new TaskSpec(TaskId.New(), "Plan the work.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement the work.", AgentRole.Developer);

        Xunit.Assert.True(classifier.AllowsNoChangeCompletion(planner, "NO_CHANGE: current design is sufficient", string.Empty));
        Xunit.Assert.False(classifier.AllowsNoChangeCompletion(developer, "NO_CHANGE: current design is sufficient", string.Empty));
        Xunit.Assert.False(classifier.AllowsNoChangeCompletion(planner, "No implementation supplied.", string.Empty));
    }

    [Xunit.Fact]
    public void DeveloperNoChangeRequiresCandidateBoundTypedReceiptAndPassingWorkerResult()
    {
        var classifier = CreateClassifier();
        var candidate = new string('c', 40);
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement the work.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Converge without edits", [developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            "C:\\repo",
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            BaseCommit: candidate,
            ContextPackageReceipt: new WorkerContextPackageReceipt(
                "ctxpkg-test",
                [],
                ProviderUsageValue.Unknown("test"),
                ProviderUsageValue.Unknown("test"),
                ProviderUsageValue.Unknown("test"),
                EarlyConvergenceEligible: true,
                EarlyConvergenceCandidateSha: candidate,
                EarlyConvergenceReceiptHashes: [new string('a', 64)])));
        var passing = WorkerResultBlock("none", "Invoke-TestSummary", "pass - focused verification completed");
        var deferred = WorkerResultBlock("none", "none", "deferred - acceptance gate");

        Xunit.Assert.True(classifier.AllowsNoChangeCompletion(developer, passing, string.Empty));
        Xunit.Assert.False(classifier.AllowsNoChangeCompletion(developer, deferred, string.Empty));
    }

    [Xunit.Fact]
    public void VerificationClassificationDistinguishesPassingAndFailingWorkerResults()
    {
        var classifier = CreateClassifier();
        var passing = WorkerResultBlock("none", "dotnet test", "pass - 5/5");
        var failing = WorkerResultBlock("none", "dotnet test", "fail - 1 failed");

        Xunit.Assert.True(classifier.HasClassifiedVerificationEvidence(passing, string.Empty));
        Xunit.Assert.True(classifier.HasCompletedVerification(passing, string.Empty));
        Xunit.Assert.False(classifier.HasReportedFailingVerification(passing, string.Empty));
        Xunit.Assert.False(classifier.HasCompletedVerification(failing, string.Empty));
        Xunit.Assert.True(classifier.HasReportedFailingVerification(failing, string.Empty));
    }

    [Xunit.Fact]
    public void FailedWorkerBuildCheckReturnsExactDiagnostic()
    {
        var classifier = CreateClassifier();
        var tests = "FAIL build: 1 error(s) (Invoke-WorkerBuildCheck) src/Feature.cs(10,20): error CS1002: ; expected";
        var output = WorkerResultBlock("src/Feature.cs", ".\\scripts\\Invoke-WorkerBuildCheck.ps1 src\\Feature.csproj", tests);

        var classified = classifier.TryFindFailedWorkerBuildCheck("unused", output, string.Empty, out var diagnostic);

        Xunit.Assert.True(classified);
        Xunit.Assert.Equal($"WORKER_RESULT reported failed worker build check: {tests}", diagnostic);
    }

    [Xunit.Fact]
    public void BuildEvidencePrefersCompleteStdoutResultOverRetainedStderrResult()
    {
        var classifier = CreateClassifier();
        var stdout = WorkerResultBlock(
            "src/Feature.cs",
            ".\\scripts\\Invoke-WorkerBuildCheck.ps1 src\\Feature.csproj",
            "deferred - acceptance gate owns execution");
        var retainedStderr = WorkerResultBlock(
            "src/OldFeature.cs",
            ".\\scripts\\Invoke-WorkerBuildCheck.ps1 src\\OldFeature.csproj",
            "pass - build: 0 errors (Invoke-WorkerBuildCheck)");

        Xunit.Assert.False(classifier.HasWorkerBuildEvidence("unused", stdout, retainedStderr));
        Xunit.Assert.True(classifier.HasWorkerBuildEvidence("unused", string.Empty, retainedStderr));
    }

    [Xunit.Fact]
    public void SandboxCommitBlockedAndLowIntegrityConfinementAreClassifiedIndependently()
    {
        var classifier = CreateClassifier();
        var sandboxFailure = "fatal: Unable to create '.git/index.lock': Permission denied";
        var dispatch = new TaskDispatchRecord(
            "codex-cli",
            "codex exec prompt",
            "worktree",
            DateTimeOffset.Parse("2026-08-22T12:00:00Z"),
            SandboxLowIntegrity: true);
        var process = ProcessRecord("worktree");
        var sandboxPrep = "{\"event\":\"sandbox-prep\",\"phase\":\"complete\"}";

        Xunit.Assert.True(classifier.HasSandboxCommitBlockedEvidence(
            AgentRole.Developer,
            WorkerResultBlock("src/Feature.cs", "git commit", "not-run - commit blocked by sandbox"),
            sandboxFailure));
        Xunit.Assert.True(classifier.HasLowIntegrityConfinementEvidence(dispatch, process, sandboxPrep, false));
        Xunit.Assert.False(classifier.HasLowIntegrityConfinementEvidence(null, process, sandboxPrep, true));
    }

    [Xunit.Fact]
    public void ProviderFailureUsesInjectedResolverAndNullDispatchReturnsUnknown()
    {
        var catalog = WorkerProviderCatalog.Default();
        var resolverCalled = false;
        var classifier = CreateClassifier(resolveWorkerProvider: dispatch =>
        {
            resolverCalled = true;
            return catalog.ResolveProfile(dispatch.WorkerName);
        });
        var dispatch = new TaskDispatchRecord(
            "codex-cli",
            "codex exec prompt",
            "worktree",
            DateTimeOffset.Parse("2026-08-22T12:00:00Z"));

        var failure = classifier.ParseProviderFailureKind(
            dispatch,
            1,
            string.Empty,
            "CreateProcessAsUserW 1312: A specified logon session does not exist.");

        Xunit.Assert.True(resolverCalled);
        Xunit.Assert.Equal(ProviderFailureKind.Sandbox1312, failure);
        resolverCalled = false;
        Xunit.Assert.Equal(ProviderFailureKind.Unknown, classifier.ParseProviderFailureKind(null, 1, string.Empty, "failure"));
        Xunit.Assert.False(resolverCalled);
    }

    [Xunit.Fact]
    public void RoleCommitEvidenceClassificationPreservesTesterVerificationException()
    {
        var classifier = CreateClassifier();
        var developer = new TaskSpec(TaskId.New(), "Implement the work.", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Verify the work.", AgentRole.Tester);
        var planner = new TaskSpec(TaskId.New(), "Plan the work.", AgentRole.Planner);

        Xunit.Assert.True(classifier.RequiresPostDispatchCommitEvidence(developer, false));
        Xunit.Assert.True(classifier.RequiresPostDispatchCommitEvidence(tester, false));
        Xunit.Assert.False(classifier.RequiresPostDispatchCommitEvidence(tester, true));
        Xunit.Assert.False(classifier.RequiresPostDispatchCommitEvidence(planner, false));
        Xunit.Assert.True(classifier.IsVerificationOnlyTesterCompletion(tester, string.Empty, true));
        Xunit.Assert.False(classifier.IsVerificationOnlyTesterCompletion(developer, string.Empty, true));
    }

    [Xunit.Fact]
    public void WrapperReconciliationPredicateRequiresEveryCompletionGate()
    {
        var evidence = new BackgroundDispatchRunner.WrapperExitReconciliationEvidence(
            ObservedRootExitCode: 1,
            ChildExitCode: 0,
            HasCompleteNonBlockedWorkerResult: true,
            CompletionContractSucceeded: true,
            HasKnownRoleCapability: true,
            RoleCapability: DispatchRoleOutputCapability.RequiresChangeEvidence,
            HasRelevantChangeEvidence: true,
            HasTerminalHumanInputDirective: false,
            HasFatalOrchestratorFailure: false);

        Xunit.Assert.True(WorkerDispatchCompletionClassifier.ShouldReconcileWrapperExit(evidence));
        Xunit.Assert.False(WorkerDispatchCompletionClassifier.ShouldReconcileWrapperExit(
            evidence with { HasFatalOrchestratorFailure = true }));
    }

    [Xunit.Fact]
    public void ReconciliationOriginPrefersRecordedStdoutRule()
    {
        var classifier = CreateClassifier();
        var task = new TaskSpec(TaskId.New(), "Verify the work.", AgentRole.Tester);
        var process = ProcessRecord("worktree");
        var childExit = new DispatchProcessHost.DispatchChildExitRecord(
            42,
            0,
            DateTimeOffset.Parse("2026-08-22T12:00:01Z"));

        var rule = classifier.ClassifyReconciliationOriginRule(
            task,
            process,
            1,
            "CLASSIFIER rule=stdout-origin; outcome_class=environmental",
            "CLASSIFIER rule=stderr-origin; outcome_class=environmental",
            null,
            true,
            false,
            ProviderFailureKind.Unknown,
            childExit,
            null);

        Xunit.Assert.Equal("stdout-origin", rule);
    }

    private static WorkerDispatchCompletionClassifier CreateClassifier(
        Func<string, bool>? fileExists = null,
        Func<string, string>? readArtifactText = null,
        Func<TaskDispatchRecord, IWorkerProvider>? resolveWorkerProvider = null)
    {
        var catalog = WorkerProviderCatalog.Default();
        return new WorkerDispatchCompletionClassifier(
            resolveWorkerProvider ?? (dispatch => catalog.ResolveProfile(dispatch.WorkerName)),
            new FixedClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z")),
            fileExists ?? (_ => false),
            readArtifactText ?? (_ => throw new FileNotFoundException()));
    }

    private static TaskProcessRecord ProcessRecord(string workingDirectory) =>
        new(
            7,
            "worker command",
            workingDirectory,
            Path.Combine(workingDirectory, "stdout.log"),
            Path.Combine(workingDirectory, "stderr.log"),
            Path.Combine(workingDirectory, "exit.txt"),
            DateTimeOffset.Parse("2026-08-22T11:59:00Z"),
            null,
            null);

    private static string WorkerResultBlock(string files, string commands, string tests) =>
        $"""
        WORKER_RESULT:
        files: {files}
        commands: {commands}
        tests: {tests}
        commit: none
        blockers: none
        model_fit: OpenAI/{AgentCatalog.OpenAiSolSubscriptionModelAlias} - adequate - test fixture
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
