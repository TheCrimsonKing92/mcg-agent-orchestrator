using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;

public sealed class ProviderBudgetExhaustionClassifierTests : WorkerDispatchTestSupport
{
    private const string ObservedDiagnostic =
        "API error (status 402 Payment Required): Grok Build usage balance exhausted";
    private const string GenericBudgetDiagnostic =
        "API error (status 402 Payment Required): account usage balance exhausted";
    private const string CredentialBinding = "C:\\profiles\\funded-a";
    private static readonly WorkerSandboxOptions EnabledSandbox = new(
        true,
        WorkerSandboxOptions.DefaultAccount,
        WorkerSandboxOptions.DefaultCredentialTarget);

    [Xunit.Fact]
    public void WorkerProviderCatalogRecognizesGrokBalanceExhaustion()
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("grok-cli");

        Assert.NotEqual(ProviderKind.Unknown, provider.Identity.Kind);

        var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(
            1,
            string.Empty,
            ObservedDiagnostic));

        Assert.Equal("BudgetExhausted", failureKind.ToString());
    }

    [Xunit.Theory]
    // ANSI framing is constructed around the verbatim observed message because log retention removed the original bytes.
    [Xunit.InlineData("\u001b[31mAPI error (status 402 Payment Required): Grok Build usage balance exhausted\u001b[0m")]
    [Xunit.InlineData("{\"error\":{\"message\":\"Grok Build usage balance exhausted\",\"http_status\":402}}")]
    [Xunit.InlineData("{\n  \"error\": {\n    \"message\": \"Grok Build usage balance exhausted\",\n    \"http_status\": 402\n  }\n}")]
    [Xunit.InlineData("{\n  \"http_status\": 402,\n  \"error\": {\n    \"message\": \"Grok Build usage balance exhausted\"\n  }\n}")]
    public void WorkerProviderRecognizesConstructedAnsiAndStructuredWrapper(string standardError)
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("grok-cli");

        var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(1, string.Empty, standardError));

        Assert.Equal(ProviderFailureKind.BudgetExhausted, failureKind);
    }

    [Xunit.Fact]
    public void WorkerProviderRecognizesStructuredWrapperWithSurroundingStderr()
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("grok-cli");
        var standardError =
            "provider bootstrap {not-json}\n" +
            "{\n  \"error\": {\n    \"http_status\": 402,\n    \"message\": \"Grok Build usage balance exhausted\"\n  }\n}\n" +
            "provider shutdown complete";

        var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(1, string.Empty, standardError));

        Assert.Equal(ProviderFailureKind.BudgetExhausted, failureKind);
    }

    [Xunit.Theory]
    [Xunit.InlineData("API error (status 402 Payment Required): request rejected", ProviderFailureKind.Unknown)]
    [Xunit.InlineData("ERROR: HTTP 401 Unauthorized", ProviderFailureKind.Unknown)]
    [Xunit.InlineData("tests failed: expected 402 but got 500", ProviderFailureKind.Unknown)]
    [Xunit.InlineData("ERROR: HTTP 429 Too Many Requests", ProviderFailureKind.RateLimit)]
    [Xunit.InlineData("connection refused", ProviderFailureKind.Connectivity)]
    public void WorkerProviderKeepsBudgetExhaustionDistinct(
        string standardError,
        ProviderFailureKind expected)
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("grok-cli");

        var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(1, string.Empty, standardError));

        Assert.Equal(expected, failureKind);
    }

    [Xunit.Fact]
    public void WorkerProviderIgnoresQuotedBudgetDiagnosticInWorkerProse()
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("grok-cli");

        var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(1, ObservedDiagnostic, string.Empty));

        Assert.Equal(ProviderFailureKind.Unknown, failureKind);
    }

    [Xunit.Fact]
    public void WorkerProviderIgnoresBudgetFragmentsAcrossUnrelatedStderrLines()
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("grok-cli");

        var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(
            1,
            string.Empty,
            "prior response status was 402 Payment Required\nunrelated note: usage balance exhausted"));

        Assert.Equal(ProviderFailureKind.Unknown, failureKind);
    }

    [Xunit.Fact]
    public void WorkerProviderIgnoresBudgetDiagnosticInsideStderrWorkerResultBlock()
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("grok-cli");

        var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(
            1,
            string.Empty,
            $"WORKER_RESULT:\nblockers: quoted log: {ObservedDiagnostic}\nEND_WORKER_RESULT"));

        Assert.Equal(ProviderFailureKind.Unknown, failureKind);
    }

    [Xunit.Fact]
    public void CompletionPreservesBudgetExhaustionAheadOfMissingFileChange()
    {
        var (_, task, dispatchedAt) = DispatchedTask();
        var verification = FailedVerification(
            dispatchedAt,
            $"{ObservedDiagnostic}\n{DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing)}",
            ProviderFailureKind.BudgetExhausted);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Assert.Equal(DispatchOutcomeKind.ProviderBudgetExhausted, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Null(outcome.RetryAfter);
        Assert.Null(outcome.Cooldown);
        Assert.Contains("rule=provider-budget-exhausted", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.DoesNotContain("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.Contains("source=stderr", outcome.EvidenceSummary, StringComparison.Ordinal);
        Assert.Contains("binding=xai::<provider-default>", outcome.EvidenceSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void BudgetExhaustionOutranksConnectivityChatter()
    {
        var (_, task, dispatchedAt) = DispatchedTask();
        var verification = FailedVerification(
            dispatchedAt,
            $"connection refused while reporting provider response\n{ObservedDiagnostic}",
            ProviderFailureKind.BudgetExhausted);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Assert.Equal(DispatchOutcomeKind.ProviderBudgetExhausted, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Contains("rule=provider-budget-exhausted", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SuccessfulWorkerEvidenceOutranksUnrelatedProviderChatter()
    {
        var (_, task, dispatchedAt) = DispatchedTask();
        var verification = new TaskVerificationRecord(
            "grok",
            "C:\\repo",
            1,
            WorkerResultBlock("src/A.cs", "build", "pass - 1 passed", commit: "abc123"),
            ObservedDiagnostic,
            dispatchedAt.AddMinutes(1),
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            ProviderFailureKind: ProviderFailureKind.BudgetExhausted,
            DispatchStartedAt: dispatchedAt,
            AssignedScopeComplete: true);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Assert.Contains("rule=committed-worker-result-evidence", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DisabledRecognitionReproducesOldDispositionAndNoAdmissionHold()
    {
        var provider = new StaticWorkerProvider(
            new WorkerProviderIdentity(ProviderKind.XaiGrokCli, UsesCodexExitFileBehavior: false),
            "grok-cli",
            "xAI",
            new WorkerCapabilities(true, true, true, true),
            recognizeBudgetExhaustion: false);
        var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(1, string.Empty, ObservedDiagnostic));
        var (kernel, task, dispatchedAt) = DispatchedTask();
        var verification = FailedVerification(
            dispatchedAt,
            $"{ObservedDiagnostic}\n{DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing)}",
            failureKind);

        var outcome = DispatchFailureClassifier.Classify(task, verification);
        kernel.RecordTaskVerification(task.LastDispatch!.GoalId!, task.Id, verification);

        Assert.Equal(ProviderFailureKind.Unknown, failureKind);
        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Contains("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.False(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            kernel.Goals,
            "xAI",
            credentialBinding: null,
            out _));
    }

    [Xunit.Fact]
    public void BudgetExhaustionRequiresOperatorTriageAndNeverAutomaticRetry()
    {
        var repository = CreateSeededDispatchRepository();
        try
        {
            var (kernel, task, dispatchedAt) = DispatchedTask();
            var verification = FailedVerification(
                dispatchedAt,
                ObservedDiagnostic,
                ProviderFailureKind.BudgetExhausted);
            kernel.RecordTaskVerification(task.LastDispatch!.GoalId!, task.Id, verification);
            var outcome = DispatchFailureClassifier.Classify(task, verification);

            var item = FailureTriagePlanner.Build(
                    kernel,
                    kernel.GetGoal(task.LastDispatch.GoalId!),
                    [XaiDeveloperAgent()],
                    repository,
                    AutonomyPolicy.Observe)
                .Items
                .Single(candidate => candidate.TaskId == task.Id);

            Assert.Null(AutomaticWorkerRetryCause.Resolve(task, outcome));
            Assert.Equal(FailureTriageCause.ProviderBudgetExhausted, item.Cause);
            Assert.Equal(FailureTriageAction.RequestHumanInput, item.Action);
            Assert.True(item.RequiresOperatorGate);
            Assert.Contains("binding=xai::<provider-default>", item.Explanation, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(repository, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void ExhaustedBindingHoldPersistsAcrossSnapshotAndClearsOnFreshSuccess()
    {
        var kernel = new AgentOrchestratorKernel();
        var sourceGoal = kernel.CreateGoal(
            "Observe exhausted xAI binding",
            [new TaskSpec(TaskId.New(), "Run provider work.", AgentRole.Developer)]);
        var recoveryGoal = kernel.CreateGoal(
            "Observe incidental provider recovery",
            [new TaskSpec(TaskId.New(), "Run later provider work.", AgentRole.Developer)]);
        var unrelatedGoal = kernel.CreateGoal(
            "Continue unrelated provider work",
            [new TaskSpec(TaskId.New(), "Run OpenAI work.", AgentRole.Developer)]);
        var xaiAgent = XaiDeveloperAgent();
        kernel.ActivateGoal(sourceGoal.Id, [xaiAgent]);
        kernel.ActivateGoal(recoveryGoal.Id, [xaiAgent]);
        kernel.ActivateGoal(unrelatedGoal.Id, [SubscriptionDeveloperAgent()]);
        var sourceTask = sourceGoal.Tasks.Single();
        var sourceDispatchAt = DateTimeOffset.Parse("2026-09-06T19:24:00Z");
        kernel.RecordTaskDispatch(sourceGoal.Id, sourceTask.Id, GrokDispatch(sourceGoal.Id, sourceDispatchAt));
        kernel.RecordTaskVerification(
            sourceGoal.Id,
            sourceTask.Id,
            FailedVerification(sourceDispatchAt, ObservedDiagnostic, ProviderFailureKind.BudgetExhausted));
        kernel.ReportTaskProgress(
            sourceGoal.Id,
            sourceTask.Id,
            WorkTaskStatus.Failed,
            "Provider budget exhausted; operator recovery required.");

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredRecoveryGoal = restored.GetGoal(recoveryGoal.Id);

        Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            restored.Goals,
            "xAI",
            credentialBinding: null,
            out var hold));
        Assert.Equal("provider-only", hold.BindingScope);
        Assert.Equal(sourceGoal.Id, hold.SourceGoalId);
        Assert.False(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            restored.Goals,
            "OpenAI",
            credentialBinding: null,
            out _));

        var heldPlan = SubscriptionPlanBuilder.Build(
            restoredRecoveryGoal,
            [xaiAgent],
            WorkerProfileCatalog.Default(),
            commandExists: _ => true,
            providerHoldScope: restored.Goals);
        var heldItem = Assert.Single(heldPlan.Items);
        Assert.False(heldItem.CanPrepare);
        Assert.Contains("Provider budget exhausted", heldItem.Detail, StringComparison.Ordinal);
        Assert.Contains(hold.EvidenceReceipt, heldItem.Detail, StringComparison.Ordinal);

        var unrelatedPlan = SubscriptionPlanBuilder.Build(
            restored.GetGoal(unrelatedGoal.Id),
            [SubscriptionDeveloperAgent()],
            WorkerProfileCatalog.Default(),
            commandExists: _ => true,
            providerHoldScope: restored.Goals);
        Assert.True(Assert.Single(unrelatedPlan.Items).CanPrepare);

        var operatorRecovered = WithGoalStatus(
            AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()),
            sourceGoal.Id,
            GoalStatus.Failed);
        Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            operatorRecovered.Goals,
            "xAI",
            credentialBinding: null,
            out _));
        operatorRecovered.RetryTask(
            sourceGoal.Id,
            sourceTask.Id,
            "Operator confirmed the named billing binding was replenished.",
            RetryCause.ProviderBudgetRecovery);
        Assert.False(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            operatorRecovered.Goals,
            "xAI",
            credentialBinding: null,
            out _));
        var recoveryRoundTrip = AgentOrchestratorKernel.FromSnapshot(operatorRecovered.ExportSnapshot());
        recoveryRoundTrip.RetryTaskAutomatically(
            sourceGoal.Id,
            sourceTask.Id,
            "Later automatic retry must not erase the explicit budget recovery marker.",
            RetryCause.ProviderInterruption);
        Assert.False(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            recoveryRoundTrip.Goals,
            "xAI",
            credentialBinding: null,
            out _));

        var recoveryTask = restoredRecoveryGoal.Tasks.Single();
        var recoveryDispatchAt = DateTimeOffset.Parse("2026-09-06T20:00:00Z");
        restored.RecordTaskDispatch(recoveryGoal.Id, recoveryTask.Id, GrokDispatch(recoveryGoal.Id, recoveryDispatchAt));
        restored.RecordTaskVerification(
            recoveryGoal.Id,
            recoveryTask.Id,
            new TaskVerificationRecord(
                "grok",
                "C:\\repo",
                0,
                "provider request completed",
                string.Empty,
                recoveryDispatchAt.AddMinutes(1),
                DispatchStartedAt: recoveryDispatchAt));

        Assert.False(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            restored.Goals,
            "xAI",
            credentialBinding: null,
            out _));
    }

    [Xunit.Fact]
    public void AutomaticProviderRetryDoesNotClearExhaustedBindingHold()
    {
        var (kernel, task, dispatchedAt) = DispatchedTask();
        kernel.RecordTaskVerification(
            task.LastDispatch!.GoalId!,
            task.Id,
            FailedVerification(dispatchedAt, ObservedDiagnostic, ProviderFailureKind.BudgetExhausted));
        kernel.ReportTaskProgress(
            task.LastDispatch.GoalId!,
            task.Id,
            WorkTaskStatus.Failed,
            "Provider budget exhausted; operator recovery required.");

        kernel.RetryTaskAutomatically(
            task.LastDispatch.GoalId!,
            task.Id,
            "Automatic provider interruption retry.",
            RetryCause.ProviderInterruption);

        Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            kernel.Goals,
            "xAI",
            credentialBinding: null,
            out _));
    }

    [Xunit.Fact]
    public void ExplicitRecoveryClearsOnlyTheLatestNamedCredentialBinding()
    {
        var clock = new BudgetTestClock(DateTimeOffset.Parse("2026-09-06T19:20:00Z"));
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Recover one exhausted credential",
            [new TaskSpec(TaskId.New(), "Run provider work.", AgentRole.Developer)]);
        var agent = ClaudeDeveloperAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var firstDispatchAt = DateTimeOffset.Parse("2026-09-06T19:24:00Z");
        var secondDispatchAt = firstDispatchAt.AddMinutes(10);
        RecordClaudeBudgetExhaustion(kernel, goal, task, firstDispatchAt, @"C:\profiles\funded-a");
        kernel.ReportTaskProgress(
            goal.Id,
            task.Id,
            WorkTaskStatus.Failed,
            "First credential exhausted.");
        clock.UtcNow = firstDispatchAt.AddMinutes(2);
        kernel.RetryTaskAutomatically(
            goal.Id,
            task.Id,
            "Try the separately funded credential.",
            RetryCause.ProviderInterruption);
        RecordClaudeBudgetExhaustion(kernel, goal, task, secondDispatchAt, @"C:\profiles\funded-b");
        kernel.ReportTaskProgress(
            goal.Id,
            task.Id,
            WorkTaskStatus.Failed,
            "Provider budget exhausted; operator recovery required.");

        clock.UtcNow = secondDispatchAt.AddMinutes(2);
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Operator replenished funded-b.",
            RetryCause.ProviderBudgetRecovery);

        Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            kernel.Goals,
            "Anthropic",
            @"C:\profiles\funded-a",
            out _));
        Assert.False(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            kernel.Goals,
            "Anthropic",
            @"C:\profiles\funded-b",
            out _));
    }

    [Xunit.Fact]
    public void OperatorInboxSeesCrossGoalCredentialScopedHold()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var sourceGoal = kernel.CreateGoal(
                "Observe exhausted xAI binding",
                [new TaskSpec(TaskId.New(), "Run provider work.", AgentRole.Developer)]);
            var targetGoal = kernel.CreateGoal(
                "Avoid the exhausted xAI binding",
                [new TaskSpec(TaskId.New(), "Run later provider work.", AgentRole.Developer)]);
            var agent = XaiDeveloperAgent();
            kernel.ActivateGoal(sourceGoal.Id, [agent]);
            kernel.ActivateGoal(targetGoal.Id, [agent]);
            var sourceTask = sourceGoal.Tasks.Single();
            var dispatchedAt = DateTimeOffset.Parse("2026-09-06T19:24:00Z");
            kernel.RecordTaskDispatch(sourceGoal.Id, sourceTask.Id, GrokDispatch(sourceGoal.Id, dispatchedAt));
            kernel.RecordTaskVerification(
                sourceGoal.Id,
                sourceTask.Id,
                FailedVerification(dispatchedAt, ObservedDiagnostic, ProviderFailureKind.BudgetExhausted));
            var workspace = OrchestratorWorkspace.ForDirectory(root);

            var inbox = OperatorInbox.Build(
                kernel,
                [agent],
                WorkerProfileCatalog.Default(),
                workspace,
                targetGoal.Id.Value[..8]);

            var blocked = Assert.Single(inbox.Items.Where(item =>
                item.Kind == OperatorInboxKind.SubscriptionRouteWarning));
            Assert.Equal(OperatorInboxSeverity.Blocker, blocked.Severity);
            Assert.Contains("xai::<provider-default>", blocked.Evidence, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("receipt=", blocked.Evidence, StringComparison.OrdinalIgnoreCase);
            var readiness = Assert.Single(inbox.Items.Where(item =>
                item.Kind == OperatorInboxKind.ReadinessPreflight &&
                item.Severity == OperatorInboxSeverity.Blocker &&
                item.Message.Contains("xai::<provider-default>", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("xai::<provider-default>", readiness.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void SubscriptionPlanResolvesClaudeCredentialOncePerBuild()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Plan two Claude tasks from one credential snapshot",
            [
                new TaskSpec(TaskId.New(), "Implement first change.", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Implement second change.", AgentRole.Developer)
            ]);
        var agent = ClaudeDeveloperAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        var probeCount = 0;

        _ = SubscriptionPlanBuilder.Build(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            sandboxOptions: EnabledSandbox,
            commandExists: _ => true,
            claudeAuthProbe: () =>
            {
                probeCount++;
                return CredentialAuthProbe();
            });

        Assert.Equal(1, probeCount);
    }

    [Xunit.Fact]
    public async Task ExhaustedBindingHoldRoundTripsThroughStateDatabase()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var (kernel, task, dispatchedAt) = DispatchedTask();
            kernel.RecordTaskVerification(
                task.LastDispatch!.GoalId!,
                task.Id,
                FailedVerification(dispatchedAt, ObservedDiagnostic, ProviderFailureKind.BudgetExhausted));
            var stateDbPath = Path.Combine(root, "state.db");
            _ = StateDbMigrations.EnsureUpToDate(stateDbPath);
            var repository = new SqliteOrchestratorStateRepository(stateDbPath);

            await repository.SaveAsync(kernel);
            var restored = await repository.LoadAsync();

            Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
                restored.Goals,
                "xAI",
                credentialBinding: null,
                out var hold));
            Assert.Equal(task.Id, hold.SourceTaskId);
            Assert.Equal("provider-only", hold.BindingScope);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void CredentialScopedHoldBlocksSubscriptionPlan()
    {
        var (kernel, targetGoal, agent) = CredentialScopedHoldScenario();

        var plan = SubscriptionPlanBuilder.Build(
            targetGoal,
            [agent],
            WorkerProfileCatalog.Default(),
            sandboxOptions: EnabledSandbox,
            commandExists: _ => true,
            providerHoldScope: kernel.Goals,
            claudeAuthProbe: CredentialAuthProbe);

        var item = Assert.Single(plan.Items);
        Assert.False(item.CanPrepare, item.Detail);
        Assert.Contains("anthropic::c:\\profiles\\funded-a", item.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            item.Route!.Reasons,
            reason => reason.Contains("anthropic::c:\\profiles\\funded-a", StringComparison.OrdinalIgnoreCase) &&
                reason.Contains("receipt=", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void CrossGoalCredentialScopedHoldBlocksReadyBatch()
    {
        var repository = CreateSeededDispatchRepository();
        try
        {
            var promptRoot = Path.Combine(repository, ".test-prompts");
            var (kernel, targetGoal, agent) = CredentialScopedHoldScenario();
            var worktree = GoalWorktrees.Ensure(repository, targetGoal.Id);

            var batch = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
                kernel,
                targetGoal,
                [agent],
                WorkerProfileCatalog.Default(),
                promptRoot,
                worktree,
                DateTimeOffset.Parse("2026-09-06T20:00:00Z"),
                commandExists: _ => true,
                sandboxOptions: EnabledSandbox,
                claudeAuthProbe: CredentialAuthProbe);

            Assert.Empty(batch.Dispatches);
            var blocked = Assert.Single(batch.Blocked);
            Assert.Equal("provider-budget-exhausted", blocked.Reason);
            Assert.Contains(
                blocked.Details ?? [],
                detail => detail.Contains("anthropic::c:\\profiles\\funded-a", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(repository, recursive: true); } catch { }
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal TargetGoal, AgentDefinition Agent) CredentialScopedHoldScenario()
    {
        var kernel = new AgentOrchestratorKernel();
        var sourceGoal = kernel.CreateGoal(
            "Observe exhausted Anthropic binding",
            [new TaskSpec(TaskId.New(), "Run provider work.", AgentRole.Developer)]);
        var targetGoal = kernel.CreateGoal(
            "Avoid the exhausted Anthropic binding",
            [new TaskSpec(TaskId.New(), "Run later provider work.", AgentRole.Developer)]);
        var agent = ClaudeDeveloperAgent();
        kernel.ActivateGoal(sourceGoal.Id, [agent]);
        kernel.ActivateGoal(targetGoal.Id, [agent]);
        var sourceTask = sourceGoal.Tasks.Single();
        var dispatchedAt = DateTimeOffset.Parse("2026-09-06T19:24:00Z");
        kernel.RecordTaskDispatch(
            sourceGoal.Id,
            sourceTask.Id,
            new TaskDispatchRecord(
                "claude-cli",
                "claude",
                "C:\\repo",
                dispatchedAt,
                ProviderName: "Anthropic",
                WorkerProviderKind: ProviderKind.AnthropicClaudeCli,
                ClaudeCredentialSourceDirectory: CredentialBinding,
                ClaudeCredentialSourceIsExplicit: true,
                GoalId: sourceGoal.Id));
        kernel.RecordTaskVerification(
            sourceGoal.Id,
            sourceTask.Id,
            FailedVerification(dispatchedAt, GenericBudgetDiagnostic, ProviderFailureKind.BudgetExhausted));
        kernel.ReportTaskProgress(
            sourceGoal.Id,
            sourceTask.Id,
            WorkTaskStatus.Failed,
            "Provider budget exhausted; operator recovery required.");
        return (kernel, targetGoal, agent);
    }

    private static ClaudeCliAuthState CredentialAuthProbe() => new(
        HasAnthropicApiKey: false,
        HasCliCredentialArtifact: true,
        CredentialArtifactPath: Path.Combine(CredentialBinding, ".credentials.json"),
        SelectedSourceDirectory: CredentialBinding,
        IsExplicitSource: true);

    private static void RecordClaudeBudgetExhaustion(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset dispatchedAt,
        string credentialBinding)
    {
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "claude-cli",
                "claude",
                "C:\\repo",
                dispatchedAt,
                ProviderName: "Anthropic",
                WorkerProviderKind: ProviderKind.AnthropicClaudeCli,
                ClaudeCredentialSourceDirectory: credentialBinding,
                ClaudeCredentialSourceIsExplicit: true,
                GoalId: goal.Id));
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            FailedVerification(dispatchedAt, GenericBudgetDiagnostic, ProviderFailureKind.BudgetExhausted));
    }

    private static (AgentOrchestratorKernel Kernel, TaskSpec Task, DateTimeOffset DispatchedAt) DispatchedTask()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Classify provider budget exhaustion",
            [new TaskSpec(TaskId.New(), "Implement provider behavior.", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, [SubscriptionDeveloperAgent()]);
        var task = goal.Tasks.Single();
        var dispatchedAt = DateTimeOffset.Parse("2026-09-06T19:24:00Z");
        kernel.RecordTaskDispatch(goal.Id, task.Id, GrokDispatch(goal.Id, dispatchedAt));
        return (kernel, task, dispatchedAt);
    }

    private static TaskDispatchRecord GrokDispatch(GoalId goalId, DateTimeOffset dispatchedAt) => new(
        "grok-cli",
        "grok",
        "C:\\repo",
        dispatchedAt,
        ProviderName: "xAI",
        WorkerProviderKind: ProviderKind.XaiGrokCli,
        GoalId: goalId);

    private static TaskVerificationRecord FailedVerification(
        DateTimeOffset dispatchedAt,
        string standardError,
        ProviderFailureKind failureKind) => new(
        "grok",
        "C:\\repo",
        1,
        string.Empty,
        standardError,
        dispatchedAt.AddMinutes(1),
        ProviderFailureKind: failureKind,
        DispatchStartedAt: dispatchedAt);

    private static AgentDefinition XaiDeveloperAgent() => new(
        new AgentId("xai-developer"),
        "xAI Developer",
        AgentRole.Developer,
        new ModelProfile(
            "xAI",
            "grok-4.6",
            ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
            SubscriptionMode.ApiKey,
            "high"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("grok-cli", "grok-4.6", "high"));

    private static AgentDefinition ClaudeDeveloperAgent() => new(
        new AgentId("claude-developer"),
        "Claude Developer",
        AgentRole.Developer,
        new ModelProfile(
            "Anthropic",
            "claude-opus-5",
            ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
            SubscriptionMode.ApiKey,
            "high"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-opus-5", "high"));

    private sealed class BudgetTestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
