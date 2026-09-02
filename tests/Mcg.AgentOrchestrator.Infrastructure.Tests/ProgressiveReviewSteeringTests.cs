using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text;
using System.Text.RegularExpressions;

[Collection(TestCollections.ChaosGateGit)]
public sealed class ProgressiveReviewSteeringTests
{
    private const string DeliberatelyDifferentModelAlias = "gpt-other"; // Deliberate non-catalog alias used to exercise model-drift fallback.

    public static TheoryData<string, AgentRole, string> DefaultCatalogFixtureDispatches => new()
    {
        { "default-planner-dispatch-fixture", AgentRole.Planner, AgentCatalog.OpenAiSolSubscriptionModelAlias },
        { "default-ideation-dispatch-fixture", AgentRole.Ideation, AgentCatalog.OpenAiSubscriptionModelAlias },
        { "default-researcher-dispatch-fixture", AgentRole.Researcher, AgentCatalog.OpenAiSolSubscriptionModelAlias },
        { "default-developer-dispatch-fixture", AgentRole.Developer, AgentCatalog.OpenAiSolSubscriptionModelAlias },
        { "default-tester-dispatch-fixture", AgentRole.Tester, AgentCatalog.OpenAiSolSubscriptionModelAlias },
        { "default-reviewer-dispatch-fixture", AgentRole.Reviewer, AgentCatalog.OpenAiSolSubscriptionModelAlias }
    };

    [Theory(DisplayName = "ProgressiveReviewSteering_each_default_fixture_dispatch_matches_activated_agent_catalog")]
    [MemberData(nameof(DefaultCatalogFixtureDispatches))]
    public void EachDefaultFixtureDispatchMatchesActivatedAgentCatalog(
        string fixtureName,
        AgentRole role,
        string recordedModelAlias)
    {
        AssertFixtureDispatchMatchesCatalog(
            fixtureName,
            AgentCatalog.Default(),
            role,
            new TaskDispatchRecord(
                "codex-cli",
                "fixture-command",
                "fixture-worktree",
                new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
                "OpenAI",
                recordedModelAlias,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_test_alias_literals_are_catalog_bound_or_justified")]
    public void TestAliasLiteralsAreCatalogBoundOrJustified()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var testRoot = Path.Combine(repositoryRoot, "tests");
        var catalogAliases = new[]
        {
            AgentCatalog.OpenAiSubscriptionModelAlias,
            AgentCatalog.OpenAiSolSubscriptionModelAlias,
            AgentCatalog.OpenAiTerraSubscriptionModelAlias,
            AgentCatalog.OpenAiLunaSubscriptionModelAlias,
            AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias
        };

        foreach (var path in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            var relativePath = Path.GetRelativePath(repositoryRoot, path);
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var alias in catalogAliases)
                {
                    if (Regex.IsMatch(
                            lines[index],
                            $@"(?<![A-Za-z0-9-]){Regex.Escape(alias)}(?![A-Za-z0-9-])",
                            RegexOptions.CultureInvariant))
                    {
                        Assert.True(
                            lines[index].Contains("// Deliberate", StringComparison.Ordinal),
                            $"{relativePath}:{index + 1} contains bare provider alias '{alias}'. Bind it to AgentCatalog or add an inline '// Deliberate ...' justification.");
                    }
                }
            }
        }
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_fixture_dispatch_matches_activated_agent_catalog")]
    public void FixtureDispatchMatchesActivatedAgentCatalog()
    {
        var root = CreateGitRepository("mcg-steer-catalog-agreement");
        var dispatchedAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

        var (_, _, task) = RunningDeveloper(root, dispatchedAt, worktreeHead: null, sessionId: null);

        AssertFixtureDispatchMatchesCatalog(
            nameof(RunningDeveloper),
            AgentCatalog.Default(),
            AgentRole.Developer,
            task.LastDispatch!);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_fixture_catalog_mismatch_names_fixture_and_aliases")]
    public void FixtureCatalogMismatchNamesFixtureAndAliases()
    {
        const string fixtureName = "negative-control-stale-developer-fixture";
        var catalog = AgentCatalog.Default();
        var expectedAlias = catalog.GetRequired(AgentRole.Developer).Subscription!.ModelAlias;
        var staleAlias = string.Equals(expectedAlias, AgentCatalog.OpenAiSolSubscriptionModelAlias, StringComparison.OrdinalIgnoreCase)
            ? AgentCatalog.OpenAiSubscriptionModelAlias
            : AgentCatalog.OpenAiSolSubscriptionModelAlias;
        var staleDispatch = new TaskDispatchRecord(
            "codex-cli",
            "fixture-command",
            "fixture-worktree",
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            "OpenAI",
            staleAlias,
            WorkerProviderKind: ProviderKind.OpenAICodexCli);

        var exception = Assert.Throws<InvalidOperationException>(() => AssertFixtureDispatchMatchesCatalog(
            fixtureName,
            catalog,
            AgentRole.Developer,
            staleDispatch));

        Assert.Contains(fixtureName, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"records model '{staleAlias}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"catalog resolves model '{expectedAlias}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_cancels_confirms_dead_then_warm_resumes_with_guidance")]
    public void CancelsConfirmsDeadThenWarmResumesWithGuidance()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-warm");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(
            root,
            now,
            head,
            sessionId: "session-12345678",
            clockOffsetAfterDispatch: TimeSpan.FromSeconds(1));
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Keep the warm dispatch scoped",
            ["preserve the accepted implementation", "  use a conductor-owned measurement  ", "ship the operator receipt"],
            VerificationClass.TestVerifiable,
            [],
            []));
        Directory.CreateDirectory(Path.GetDirectoryName(task.LastDispatch!.PromptPath!)!);
        var originalPrompt = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        var cancelledPromptPath = task.LastDispatch.PromptPath!;
        File.WriteAllText(cancelledPromptPath, originalPrompt);
        kernel.WaiveAcceptanceCriterion(
            goal.Id,
            "use a conductor-owned measurement",
            "the worker cannot drive the real conductor:",
            "operator:miles");
        var store = new InMemoryProgressiveReviewSteeringStore();
        var intent = Intent(goal, task, now, "fix the scoped slice");
        store.EnqueueIntentAsync(intent).GetAwaiter().GetResult();
        var order = new List<string>();

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: (k, goalId, taskId) =>
            {
                order.Add("cancel");
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                WriteExitAndHeartbeat(cancelled, now.AddSeconds(1), childPid: null, ownedPids: [6001], state: "exited");
                return cancelled;
            },
            startProcess: (k, goalId, taskId) =>
            {
                order.Add("start");
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Contains(" resume 'session-12345678' -", dispatch.Command, StringComparison.Ordinal);
                Assert.Contains("--sandbox workspace-write", dispatch.Command, StringComparison.Ordinal);
                Assert.Contains("ProgressiveReviewSteer", File.ReadAllText(dispatch.PromptPath!));
                var started = new TaskProcessRecord(7001, dispatch.Command, dispatch.WorkingDirectory, "out2.log", "err2.log", "exit2.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            isProcessRunning: pid =>
            {
                order.Add($"probe:{pid}");
                return false;
            },
            getLineageDescendants: process =>
            {
                order.Add($"lineage:{process.ProcessId}");
                return [];
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState, string.Join(Environment.NewLine, result.ProgressLines));
        Assert.True(order.IndexOf("cancel") >= 0);
        Assert.True(order.LastIndexOf("probe:6001") > order.IndexOf("cancel"));
        Assert.True(
            order.IndexOf("start") > order.LastIndexOf("probe:6001"),
            $"order={string.Join(',', order)}; receipts={string.Join(" | ", store.Receipts.Select(item => item.Outcome))}; checks={string.Join(" | ", store.Receipts.SelectMany(item => item.AdmissionChecks))}");
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("warm-resume", receipt.Decision);
        Assert.Contains(receipt.AdmissionChecks, check => check.StartsWith("AcceptanceCriteriaHash:Passed:", StringComparison.Ordinal));
        Assert.Contains(receipt.AdmissionChecks, check => check.StartsWith("NoCriteriaCorrectionSinceCapture:Passed:", StringComparison.Ordinal));
        Assert.Contains("tree-dead", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Contains(receipt.AdmissionChecks, check => check.StartsWith("SameGoal:Passed:", StringComparison.Ordinal));
        var expectedCancelledInputTokens = Math.Max(1, File.ReadAllText(cancelledPromptPath).Length / 4);
        Assert.Equal(expectedCancelledInputTokens, receipt.CancelledInputTokens);
        Assert.True(receipt.SteeredInputTokens > 0);
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_typed_warm_resume_repackages_complete_context_and_guidance_with_receipt")]
    public void TypedWarmResumeRepackagesCompleteContextAndGuidanceWithReceipt()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-warm-package");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var builder = new WorkerContextPackageBuilder();
        var original = WorkerProfileDispatcher.FinalizeContextPackageWithManifest(
            builder,
            builder.Prepare(
                AgentRole.Developer,
                root,
                [WorkerContextArtifact.Create(
                    new LogicalArtifactIdentity("brief/original.md"),
                    ContextArtifactKind.OperatorInstructions,
                    Encoding.UTF8.GetBytes("complete original package bytes\n"),
                    [AgentRole.Developer],
                    ContextDeliveryMode.InlineFull,
                    ContextContractVersion.V1)]));
        var originalReceipt = WorkerContextPackageBuilder.CreateReceipt(original);
        var (kernel, goal, task) = RunningDeveloper(
            root,
            now,
            head,
            sessionId: "typed-session-12345678",
            contextPackageReceipt: originalReceipt);
        Directory.CreateDirectory(Path.GetDirectoryName(task.LastDispatch!.PromptPath!)!);
        File.WriteAllText(task.LastDispatch.PromptPath!, WorkerContextPackageBuilder.Render(original));
        var originalPromptPath = task.LastDispatch.PromptPath!;
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "typed guidance is identity-bound")).GetAwaiter().GetResult();

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: (k, goalId, taskId) =>
            {
                var cancelled = CancelWithTerminalProof(now)(k, goalId, taskId);
                File.Delete(originalPromptPath);
                return cancelled;
            },
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                var receipt = Assert.IsType<WorkerContextPackageReceipt>(dispatch.ContextPackageReceipt);
                var prompt = File.ReadAllText(dispatch.PromptPath!);
                Assert.Contains(receipt.SemanticPackageId, prompt, StringComparison.Ordinal);
                Assert.Contains("typed guidance is identity-bound", prompt, StringComparison.Ordinal);
                Assert.Contains(receipt.Sections, section => section.LogicalIdentity == "brief/original.md");
                Assert.Contains(receipt.Sections, section => section.LogicalIdentity == "steering/progressive-review-guidance.md");
                Assert.NotEqual(originalReceipt.SemanticPackageId, receipt.SemanticPackageId);
                var started = new TaskProcessRecord(
                    7010,
                    dispatch.Command,
                    dispatch.WorkingDirectory,
                    "out-package.log",
                    "err-package.log",
                    "exit-package.txt",
                    now.AddSeconds(2),
                    null,
                    null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            currentHead: head,
            utcNow: () => now.AddSeconds(10));

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState, string.Join(Environment.NewLine, result.ProgressLines));
        Assert.Equal(2, kernel.GetTask(goal.Id, task.Id).DispatchHistory.Count);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_reports_typed_repackaging_failure_through_fail_safe_path")]
    public void ReportsTypedRepackagingFailureThroughFailSafePath()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-warm-package-invalid");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var builder = new WorkerContextPackageBuilder();
        var original = WorkerProfileDispatcher.FinalizeContextPackageWithManifest(
            builder,
            builder.Prepare(
                AgentRole.Developer,
                root,
                [WorkerContextArtifact.Create(
                    new LogicalArtifactIdentity("brief/original.md"),
                    ContextArtifactKind.OperatorInstructions,
                    Encoding.UTF8.GetBytes("complete original package bytes\n"),
                    [AgentRole.Developer],
                    ContextDeliveryMode.InlineFull,
                    ContextContractVersion.V1)]));
        var (kernel, goal, task) = RunningDeveloper(
            root,
            now,
            head,
            sessionId: "typed-session-12345678",
            contextPackageReceipt: WorkerContextPackageBuilder.CreateReceipt(original));
        Directory.CreateDirectory(Path.GetDirectoryName(task.LastDispatch!.PromptPath!)!);
        File.WriteAllText(task.LastDispatch.PromptPath!, "corrupt rendered context package");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "typed guidance is identity-bound")).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root, "worker-edit.cs"), "valuable worker edit");
        var started = false;

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("start must not run");
            },
            currentHead: head,
            utcNow: () => now.AddSeconds(10));

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState, string.Join(Environment.NewLine, result.ProgressLines));
        Assert.False(started);
        Assert.Single(kernel.GetTask(goal.Id, task.Id).DispatchHistory);
        var receipt = Assert.Single(store.Receipts);
        Assert.Contains("tree-dead", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("receipt-package-mismatch", receipt.Outcome, StringComparison.Ordinal);
        Assert.Contains("steer-restart-failed", receipt.Outcome, StringComparison.Ordinal);
        Assert.Contains(result.ProgressLines, line => line.Contains("reason=restart-failed", StringComparison.Ordinal));
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.True(string.IsNullOrWhiteSpace(GitCli.Run(root, "status", "--porcelain", "--untracked-files=all").Output));
        Assert.Contains("worker-edit.cs", GitCli.Run(root, "stash", "show", "--include-untracked", "--name-only", "stash@{0}").Output, StringComparison.Ordinal);
        Assert.Matches("preserved=[0-9a-f]{40}", receipt.Outcome);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_warm_resume_preserves_reviewer_cap_and_touch_receipts")]
    public void WarmResumePreservesReviewerCapAndTouchReceipts()
    {
        var now = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-review-cap");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var anchor = new ReviewFindingLocation("src/Guard.cs", "Guard.Run", "guard");
        var cap = new ReviewRetryCapReceipt(7, 7);
        var (kernel, goal, task) = RunningDeveloper(
            root,
            now,
            head,
            sessionId: "review-session-12345678",
            role: AgentRole.Reviewer,
            reviewFindingTouchedAnchors: [anchor],
            reviewFindingTouchProofDiagnostic: "System-derived diff touched the guard anchor.",
            reviewRetryCap: cap);
        Directory.CreateDirectory(Path.GetDirectoryName(task.LastDispatch!.PromptPath!)!);
        File.WriteAllText(task.LastDispatch.PromptPath!, kernel.BuildTaskBrief(goal.Id, task.Id).Content);
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "finish the cap review")).GetAwaiter().GetResult();
        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal(cap, dispatch.ReviewRetryCap);
                Assert.Equal([anchor], dispatch.ReviewFindingTouchedAnchors);
                Assert.Equal("System-derived diff touched the guard anchor.", dispatch.ReviewFindingTouchProofDiagnostic);
                var started = new TaskProcessRecord(
                    7002,
                    dispatch.Command,
                    dispatch.WorkingDirectory,
                    "out-review.log",
                    "err-review.log",
                    "exit-review.txt",
                    now.AddSeconds(2),
                    null,
                    null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState, string.Join(Environment.NewLine, result.ProgressLines));
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_rebuilds_freshness_envelope_at_steer_time")]
    public void RebuildsFreshnessEnvelopeAtSteerTime()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-current-envelope");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var staleIntent = new ProgressiveReviewSteerIntent(
            Guid.NewGuid().ToString("n"),
            goal.Id.Value,
            task.Id.Value,
            AgentRole.Developer.ToString(),
            $"{goal.Id.Value}|{task.Id.Value}|{now.UtcTicks}",
            "glance-stale",
            "sha256:stale-glance-start-inputs",
            now,
            "diff shows wrong subsystem",
            "return to the current steering envelope",
            "ProgressiveReviewSteer guidance. stale criterion from glance start. stale diff from glance start.",
            now);
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(staleIntent).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root, "fresh.txt"), "fresh diff at steer time");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "current contract",
            ["fresh criterion at steer time"],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.RecordTaskNote(goal.Id, task.Id, "CRITERIA CORRECTION supersedes=\"old criterion\" correction=\"fresh overlay at steer time\"");
        var preparedFresh = false;

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                var prompt = File.ReadAllText(dispatch.PromptPath!);
                Assert.Contains("fresh criterion at steer time", prompt, StringComparison.Ordinal);
                Assert.Contains("fresh overlay at steer time", prompt, StringComparison.Ordinal);
                Assert.Contains("fresh diff at steer time", prompt, StringComparison.Ordinal);
                Assert.DoesNotContain("stale criterion from glance start", prompt, StringComparison.Ordinal);
                Assert.DoesNotContain("stale diff from glance start", prompt, StringComparison.Ordinal);
                var started = new TaskProcessRecord(7008, dispatch.Command, dispatch.WorkingDirectory, "out-current.log", "err-current.log", "exit-current.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatch: (k, g, t, _) =>
            {
                preparedFresh = true;
                k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                    "codex-cli",
                    "fresh-guided",
                    root,
                    now.AddSeconds(2),
                    "OpenAI",
                    AgentCatalog.OpenAiSubscriptionModelAlias,
                    WorkerProviderKind: ProviderKind.OpenAICodexCli));
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, kernel.GetGoal(goal.Id));

        Assert.True(result.MutatedTaskState);
        Assert.True(preparedFresh);
        var receipt = Assert.Single(store.Receipts);
        Assert.NotEqual(staleIntent.InputsHash, receipt.InputsHash);
        Assert.StartsWith("sha256:", receipt.InputsHash, StringComparison.Ordinal);
        Assert.Contains("fresh criterion at steer time", receipt.GuidanceText, StringComparison.Ordinal);
        Assert.Contains("fresh overlay at steer time", receipt.GuidanceText, StringComparison.Ordinal);
        Assert.Contains("fresh diff at steer time", receipt.GuidanceText, StringComparison.Ordinal);
        Assert.DoesNotContain("stale criterion from glance start", receipt.GuidanceText, StringComparison.Ordinal);
        Assert.DoesNotContain("stale diff from glance start", receipt.GuidanceText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_prompt_read_failures_fall_back_in_admission_and_receipts")]
    public void PromptReadFailuresFallBackInAdmissionAndReceipts()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-prompt-read-fallback");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        Directory.CreateDirectory(Path.GetDirectoryName(task.LastDispatch!.PromptPath!)!);
        using var originalPromptLock = LockTextFile(task.LastDispatch.PromptPath!, "locked original prompt");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "contract",
            ["current criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback despite prompt IO failure")).GetAwaiter().GetResult();
        var locks = new List<FileStream>();

        try
        {
            var coordinator = NewCoordinator(
                root,
                store,
                cancelProcess: CancelWithTerminalProof(now),
                startProcess: (k, goalId, taskId) =>
                {
                    var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                    Assert.Contains("ProgressiveReviewSteer guidance", File.ReadAllText(dispatch.PromptPath!), StringComparison.Ordinal);
                    var started = new TaskProcessRecord(7009, dispatch.Command, dispatch.WorkingDirectory, "out-locked.log", "err-locked.log", "exit-locked.txt", now.AddSeconds(2), null, null);
                    k.RecordTaskProcessStarted(goalId, taskId, started);
                    return started;
                },
                prepareFreshDispatch: (k, g, t, _) =>
                {
                    var promptPath = Path.Combine(root, ".orchestrator", "prompts", "fresh-locked.md");
                    locks.Add(LockTextFile(promptPath, "locked fresh prompt"));
                    k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                        "codex-cli",
                        "fresh-guided",
                        root,
                        now.AddSeconds(2),
                        "OpenAI",
                        AgentCatalog.OpenAiSubscriptionModelAlias,
                        PromptPath: promptPath,
                        WorkerProviderKind: ProviderKind.OpenAICodexCli));
                },
                currentHead: head);

            var result = coordinator.ExecutePending(kernel, kernel.GetGoal(goal.Id));

            Assert.True(result.MutatedTaskState);
            var receipt = Assert.Single(store.Receipts);
            Assert.Equal("fresh-dispatch", receipt.Decision);
            Assert.Equal(308, receipt.CancelledInputTokens);
            Assert.Contains(receipt.AdmissionChecks, check => check.Contains("AcceptanceCriteriaHash:Failed:spawn prompt unavailable", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var stream in locks)
                stream.Dispose();
        }
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_stale_intent_raises_attention_without_cancelling_current_round")]
    public void StaleIntentRaisesAttentionWithoutCancellingCurrentRound()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-stale");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var staleIntent = Intent(goal, task, now.AddMinutes(-10), "old-round guidance must not affect current dispatch");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(staleIntent).GetAwaiter().GetResult();
        var attentionStore = new FakeCollaborationItemStore();
        var cancelCalled = false;
        var startCalled = false;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            cancelProcess: (_, _, _) =>
            {
                cancelCalled = true;
                throw new InvalidOperationException("cancel must not run for stale intent");
            },
            startProcess: (_, _, _) =>
            {
                startCalled = true;
                throw new InvalidOperationException("start must not run for stale intent");
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(cancelCalled);
        Assert.False(startCalled);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("stale-steer-intent", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Contains("stale-round", string.Join('\n', result.ProgressLines), StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_writes_terminal_cancel_proof_after_owned_tree_is_dead")]
    public void WritesTerminalCancelProofAfterOwnedTreeIsDead()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-produced-proof");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var cancelledProcessRecord = task.LastProcess!;
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "resume after generated cancel proof")).GetAwaiter().GetResult();

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                return cancelled;
            },
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                var started = new TaskProcessRecord(7010, dispatch.Command, dispatch.WorkingDirectory, "out-produced.log", "err-produced.log", "exit-produced.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("warm-resume", receipt.Decision);
        Assert.Contains("partial dispatch receipt consumed", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.True(File.Exists(cancelledProcessRecord.ExitCodePath));
        Assert.Equal("cancelled", ProcessLogReader.ReadHeartbeat(cancelledProcessRecord).State);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_preserves_existing_terminal_heartbeat_evidence")]
    public void PreservesExistingTerminalHeartbeatEvidence()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var terminalObservedAt = now.AddSeconds(1);
        var root = CreateGitRepository("mcg-steer-preserve-terminal-proof");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var cancelledProcessRecord = task.LastProcess!;
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "preserve terminal evidence")).GetAwaiter().GetResult();

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = terminalObservedAt, WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                File.WriteAllText(cancelled.ExitCodePath, "1");
                File.WriteAllText(
                    BackgroundDispatchRunner.GetHeartbeatPath(cancelled),
                    $$"""
                    {"pid":6001,"childPid":null,"ownedPids":[6001],"ownedProcessIdentities":[{"processId":6001,"startedAt":"2026-07-20T11:59:00Z","imagePath":"C:\\workers\\worker-6001.exe"}],"state":"exited","lastObservedAt":"{{terminalObservedAt:O}}","lastProgressAt":"{{terminalObservedAt:O}}","stdoutBytes":12,"stderrBytes":3,"ownedCpuMs":4,"providerSessionId":"session-preserved","worktreeHeadSha":"head-preserved","dirtyStateHash":"dirty-preserved"}
                    """);
                return cancelled;
            },
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                var started = new TaskProcessRecord(7011, dispatch.Command, dispatch.WorkingDirectory, "out-preserved.log", "err-preserved.log", "exit-preserved.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);
        var preserved = ProcessLogReader.ReadHeartbeat(cancelledProcessRecord, now.AddMinutes(1));

        Assert.True(result.MutatedTaskState);
        Assert.Equal("warm-resume", Assert.Single(store.Receipts).Decision);
        Assert.Equal("exited", preserved.State);
        Assert.Equal(terminalObservedAt, preserved.LastObservedAt);
        Assert.Equal(terminalObservedAt, preserved.LastProgressAt);
        Assert.Equal("session-preserved", preserved.ProviderSessionId);
        Assert.Equal("head-preserved", preserved.WorktreeHeadSha);
        Assert.Equal("dirty-preserved", preserved.DirtyStateHash);
        Assert.Equal([6_001], preserved.OwnedProcessIds);
        var preservedIdentity = Assert.Single(preserved.OwnedProcessIdentities);
        Assert.Equal(6_001, preservedIdentity.ProcessId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-20T11:59:00Z"), preservedIdentity.StartedAt);
        Assert.Equal(@"C:\workers\worker-6001.exe", preservedIdentity.ImagePath);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_first_misdirection_steers_second_same_round_misdirection_attention")]
    public void FirstMisdirectionSteersSecondSameRoundMisdirectionRaisesAttention()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-cap");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        var firstIntent = Intent(goal, task, now, "first steer guidance");
        var secondIntent = Intent(goal, task, now, "second steer guidance suppressed");
        store.EnqueueIntentAsync(firstIntent).GetAwaiter().GetResult();
        store.EnqueueIntentAsync(secondIntent).GetAwaiter().GetResult();
        var attentionStore = new FakeCollaborationItemStore();
        var cancelCount = 0;
        var startCount = 0;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            cancelProcess: (k, goalId, taskId) =>
            {
                cancelCount++;
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                WriteExitAndHeartbeat(cancelled, now.AddSeconds(1), childPid: null, ownedPids: [cancelled.ProcessId], state: "exited");
                return cancelled;
            },
            startProcess: (k, goalId, taskId) =>
            {
                startCount++;
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                var started = new TaskProcessRecord(
                    7000 + startCount,
                    dispatch.Command,
                    dispatch.WorkingDirectory,
                    $"out-cap-{startCount}.log",
                    $"err-cap-{startCount}.log",
                    $"exit-cap-{startCount}.txt",
                    now.AddSeconds(2 + startCount),
                    null,
                    null,
                    OwnedProcessIds: [7000 + startCount]);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            currentHead: head);

        var firstResult = coordinator.ExecutePending(kernel, goal);
        var secondResult = coordinator.ExecutePending(kernel, goal);

        Assert.True(firstResult.MutatedTaskState);
        Assert.True(secondResult.MutatedTaskState);
        Assert.Equal(1, cancelCount);
        Assert.Equal(1, startCount);
        var firstReceipt = Assert.Single(store.Receipts.Where(receipt => receipt.IntentId == firstIntent.Id));
        Assert.Equal("warm-resume", firstReceipt.Decision);
        var secondReceipt = Assert.Single(store.Receipts.Where(receipt => receipt.IntentId == secondIntent.Id));
        Assert.Equal("operator-attention", secondReceipt.Decision);
        Assert.Equal(firstReceipt.RoundKey, secondReceipt.RoundKey);
        Assert.Equal("steer-cap-reached", secondReceipt.CancelConfirmation);
        Assert.Contains("steer-cap", string.Join('\n', secondResult.ProgressLines), StringComparison.Ordinal);
        var attention = Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
        Assert.Contains("no second steer attempted", attention.Body, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_falls_back_to_fresh_dispatch_when_admission_fails")]
    public void FallsBackToFreshDispatchWhenAdmissionFails()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-fresh");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, "not-an-ancestor", sessionId: "session-12345678");
        var runPolicy = ConductorAutonomyPolicy.Permissive with { ReviewAutoRetryStopRound = 7 };
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback guidance")).GetAwaiter().GetResult();
        var preparedFresh = false;

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                WriteExitAndHeartbeat(cancelled, now.AddSeconds(1), childPid: null, ownedPids: [6001], state: "exited");
                return cancelled;
            },
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                Assert.NotNull(dispatch.ContextPackageReceipt);
                Assert.Contains(dispatch.ContextPackageReceipt.SemanticPackageId, File.ReadAllText(dispatch.PromptPath!), StringComparison.Ordinal);
                Assert.Contains("fresh fallback guidance", File.ReadAllText(dispatch.PromptPath!), StringComparison.Ordinal);
                var started = new TaskProcessRecord(7002, dispatch.Command, dispatch.WorkingDirectory, "out3.log", "err3.log", "exit3.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatchWithPolicy: (k, g, t, guidance, policy) =>
            {
                preparedFresh = true;
                Assert.Same(runPolicy, policy);
                Assert.Contains("fresh fallback guidance", guidance, StringComparison.Ordinal);
                var builder = new WorkerContextPackageBuilder();
                var package = WorkerProfileDispatcher.FinalizeContextPackageWithManifest(
                    builder,
                    builder.Prepare(
                        AgentRole.Developer,
                        root,
                        [WorkerContextArtifact.Create(
                            new LogicalArtifactIdentity("steering/current-guidance.md"),
                            ContextArtifactKind.OperatorInstructions,
                            Encoding.UTF8.GetBytes(guidance),
                            [AgentRole.Developer],
                            ContextDeliveryMode.InlineFull,
                            ContextContractVersion.V1)]));
                var promptPath = Path.Combine(root, ".orchestrator", "prompts", "fresh-typed.md");
                Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
                File.WriteAllText(promptPath, WorkerContextPackageBuilder.Render(package));
                k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                    "codex-cli",
                    "fresh-guided",
                    root,
                    now.AddSeconds(2),
                    "OpenAI",
                    AgentCatalog.OpenAiSubscriptionModelAlias,
                    PromptPath: promptPath,
                    WorkerProviderKind: ProviderKind.OpenAICodexCli,
                    ContextPackageReceipt: WorkerContextPackageBuilder.CreateReceipt(package)));
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal, runPolicy);

        Assert.True(result.MutatedTaskState);
        Assert.True(preparedFresh);
        Assert.Equal(2, kernel.GetTask(goal.Id, task.Id).DispatchHistory.Count);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("fresh-dispatch", receipt.Decision);
        Assert.Contains(receipt.AdmissionChecks, check => check.Contains("SpawnHeadAncestor:Failed", StringComparison.Ordinal));
        Assert.Equal(head, GitCli.Run(root, "rev-parse", "HEAD").Output.Trim());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_falls_back_to_fresh_dispatch_when_acceptance_criteria_hash_changed")]
    public void FallsBackToFreshDispatchWhenAcceptanceCriteriaHashChanged()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-ac-drift");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        Directory.CreateDirectory(Path.GetDirectoryName(task.LastDispatch!.PromptPath!)!);
        File.WriteAllText(task.LastDispatch.PromptPath!, "Acceptance criteria:\n- original criterion\n");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "contract",
            ["changed criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback after AC drift")).GetAwaiter().GetResult();
        var preparedFresh = false;

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                var started = new TaskProcessRecord(7011, dispatch.Command, dispatch.WorkingDirectory, "out-ac.log", "err-ac.log", "exit-ac.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatch: (k, g, t, _) =>
            {
                preparedFresh = true;
                k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                    "codex-cli",
                    "fresh-guided",
                    root,
                    now.AddSeconds(2),
                    "OpenAI",
                    AgentCatalog.OpenAiSubscriptionModelAlias,
                    WorkerProviderKind: ProviderKind.OpenAICodexCli));
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.True(preparedFresh);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("fresh-dispatch", receipt.Decision);
        Assert.Contains(receipt.AdmissionChecks, check => check.Contains("AcceptanceCriteriaHash:Failed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_falls_back_to_fresh_dispatch_when_acceptance_criteria_were_removed_after_spawn")]
    public void FallsBackToFreshDispatchWhenAcceptanceCriteriaWereRemovedAfterSpawn()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-ac-removed");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        Directory.CreateDirectory(Path.GetDirectoryName(task.LastDispatch!.PromptPath!)!);
        File.WriteAllText(task.LastDispatch.PromptPath!, """
            ## Refined Spec
            Behavioral contract: contract

            Acceptance criteria:
            - keep criterion
            - removed criterion

            """);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "contract",
            ["keep criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback after AC removal")).GetAwaiter().GetResult();
        var preparedFresh = false;

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                var started = new TaskProcessRecord(7012, dispatch.Command, dispatch.WorkingDirectory, "out-ac-removed.log", "err-ac-removed.log", "exit-ac-removed.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatch: (k, g, t, _) =>
            {
                preparedFresh = true;
                k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                    "codex-cli",
                    "fresh-guided",
                    root,
                    now.AddSeconds(2),
                    "OpenAI",
                    AgentCatalog.OpenAiSubscriptionModelAlias,
                    WorkerProviderKind: ProviderKind.OpenAICodexCli));
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.True(preparedFresh);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("fresh-dispatch", receipt.Decision);
        Assert.Contains(receipt.AdmissionChecks, check => check.Contains("AcceptanceCriteriaHash:Failed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_falls_back_to_fresh_dispatch_for_pre_migration_parent_model")]
    public void FallsBackToFreshDispatchForPreMigrationParentModel()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-model-drift");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(
            root,
            now,
            head,
            sessionId: "session-12345678",
            dispatchedModel: AgentCatalog.OpenAiSubscriptionModelAlias);
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback after model drift")).GetAwaiter().GetResult();
        var preparedFresh = false;
        var agents = AgentCatalog.Default().Agents
            .Select(agent => agent.Role == AgentRole.Developer
                ? agent with { Subscription = agent.Subscription! with { ModelAlias = DeliberatelyDifferentModelAlias } }
                : agent)
            .ToArray();

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                var started = new TaskProcessRecord(7003, dispatch.Command, dispatch.WorkingDirectory, "out4.log", "err4.log", "exit4.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatch: (k, g, t, _) =>
            {
                preparedFresh = true;
                k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                    "codex-cli",
                    "fresh-guided",
                    root,
                    now.AddSeconds(2),
                    "OpenAI",
                    DeliberatelyDifferentModelAlias,
                    WorkerProviderKind: ProviderKind.OpenAICodexCli));
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.True(preparedFresh);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("fresh-dispatch", receipt.Decision);
        Assert.Contains(receipt.AdmissionChecks, check => check.Contains("SameModel:Failed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_falls_back_to_fresh_dispatch_when_integration_changed_after_spawn")]
    public void FallsBackToFreshDispatchWhenIntegrationChangedAfterSpawn()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-integration-drift");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        new GoalLifecycleEventWriter(
            workspace.GoalLifecycleEventsDirectory,
            new TestClock(now.AddSeconds(5))).AppendGoalLanded(goal.Id, "main", "goal/test");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback after integration changed")).GetAwaiter().GetResult();
        var preparedFresh = false;

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                var started = new TaskProcessRecord(7004, dispatch.Command, dispatch.WorkingDirectory, "out5.log", "err5.log", "exit5.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatch: (k, g, t, _) =>
            {
                preparedFresh = true;
                k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                    "codex-cli",
                    "fresh-guided",
                    root,
                    now.AddSeconds(2),
                    "OpenAI",
                    AgentCatalog.OpenAiSubscriptionModelAlias,
                    WorkerProviderKind: ProviderKind.OpenAICodexCli));
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.True(preparedFresh);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("fresh-dispatch", receipt.Decision);
        Assert.Contains(receipt.AdmissionChecks, check => check.Contains("NoIntegrationChangeSinceCapture:Failed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ProgressiveReviewSteeringStore_reserves_running_intent_after_loop_restart")]
    public void StoreReservesRunningIntentAfterLoopRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-steer-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var store = new SqliteProgressiveReviewSteeringStore(Path.Combine(root, "steering.db"));
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var intent = new ProgressiveReviewSteerIntent(
            "intent-running",
            "goal-1",
            "task-1",
            AgentRole.Developer.ToString(),
            "round-1",
            "glance-1",
            "inputs-1",
            now,
            "misdirected",
            "correct it",
            "guidance",
            now,
            ProgressiveReviewSteerIntentStatus.Running);
        store.EnqueueIntentAsync(intent).GetAwaiter().GetResult();

        var reserved = store.ReserveNextPendingAsync("goal-1").GetAwaiter().GetResult();

        Assert.NotNull(reserved);
        Assert.Equal("intent-running", reserved!.Id);
        Assert.Equal(ProgressiveReviewSteerIntentStatus.Running, reserved.Status);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_routes_to_attention_when_tree_death_is_unconfirmed")]
    public void RoutesToAttentionWhenTreeDeathIsUnconfirmed()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-attention");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "do not start while pid is alive")).GetAwaiter().GetResult();
        var attentionStore = new FakeCollaborationItemStore();
        var started = false;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            isProcessRunning: pid => pid == 6001,
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                return cancelled;
            },
            startProcess: (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("start must not run");
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("owned pid(s) still alive", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_blocks_resume_when_heartbeat_owned_child_survives")]
    public void BlocksResumeWhenHeartbeatOwnedChildSurvives()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-owned-child");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        WriteHeartbeat(task.LastProcess!, now, childPid: 6002, ownedPids: [6001, 6002, 6003], state: "running");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "do not start while child is alive")).GetAwaiter().GetResult();
        var attentionStore = new FakeCollaborationItemStore();
        var started = false;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            isProcessRunning: pid => pid == 6002,
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                return cancelled;
            },
            startProcess: (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("start must not run over a live heartbeat-owned child");
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("owned pid(s) still alive: 6002", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_blocks_resume_when_live_lineage_descendant_survives_cancel")]
    public void BlocksResumeWhenLiveLineageDescendantSurvivesCancel()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-lineage-child");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "do not start while lineage child is alive")).GetAwaiter().GetResult();
        var attentionStore = new FakeCollaborationItemStore();
        var started = false;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            isProcessRunning: pid => pid == 6102,
            getLineageDescendants: _ => [6102],
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                WriteExitAndHeartbeat(cancelled, now.AddSeconds(1), childPid: null, ownedPids: [6001], state: "exited");
                return cancelled;
            },
            startProcess: (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("start must not run over a live lineage descendant");
            },
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("owned pid(s) still alive: 6102", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_blocks_resume_when_live_lineage_identity_is_unreadable")]
    public void BlocksResumeWhenLiveLineageIdentityIsUnreadable()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-unreadable-lineage-child");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "do not start while an unreadable lineage child is alive")).GetAwaiter().GetResult();
        var attentionStore = new FakeCollaborationItemStore();
        var started = false;
        const int unreadableChildPid = 6_102;
        WindowsNativeProcessInspection.ProcessInspectionSeed[] postCancelSnapshot =
        [
            // The wrapper is gone, but Toolhelp retains the child's parent PID.
            new(unreadableChildPid, 6_001, "unreadable-child")
        ];

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            isProcessRunning: pid => pid == unreadableChildPid,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("start must not run over a live unreadable lineage descendant");
            },
            currentHead: head,
            readProcessIdentity: processId => processId == unreadableChildPid ? null : TestProcessIdentity(processId),
            listConservativeLineageDescendants: (ancestorProcessId, ancestorStartedAt) =>
                WindowsNativeProcessInspection.ListConservativeDescendantProcessIdsForRefusal(
                    ancestorProcessId,
                    ancestorStartedAt,
                    () => WindowsNativeProcessInspection.ProcessEnumerationResult.Success(postCancelSnapshot),
                    seed => new ProcessInspectionRecord(
                        seed.ProcessId,
                        seed.ParentProcessId,
                        seed.Name,
                        null,
                        null,
                        null,
                        ProcessInspectionStatus.AccessDenied)));

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("owned pid(s) still alive: 6102", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_blocks_resume_when_terminal_cancel_proof_is_absent")]
    public void BlocksResumeWhenTerminalCancelProofIsAbsent()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-missing-proof");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "do not start without terminal proof")).GetAwaiter().GetResult();
        var attentionStore = new FakeCollaborationItemStore();
        var started = false;
        File.WriteAllText(Path.Combine(root, "worker-edit.cs"), "valuable worker edit");
        var originalProcess = task.LastProcess;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                return cancelled;
            },
            startProcess: (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("start must not run without terminal proof");
            },
            options: new ProgressiveReviewSteeringOptions(WriteTerminalCancelProofArtifacts: false),
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("exit artifact missing", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
        var retained = kernel.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Assigned, retained.Status);
        Assert.Same(originalProcess, retained.LastProcess);
        Assert.True(retained.LastProcess!.IsRunning);
        Assert.True(string.IsNullOrWhiteSpace(GitCli.Run(root, "status", "--porcelain", "--untracked-files=all").Output));
        Assert.Contains("worker-edit.cs", GitCli.Run(root, "stash", "show", "--include-untracked", "--name-only", "stash@{0}").Output, StringComparison.Ordinal);
        Assert.Matches("preservation=preserved=[0-9a-f]{40}", receipt.CancelConfirmation);
        Assert.Contains("hold=retained-live-process", receipt.CancelConfirmation, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_records_receipt_and_attention_when_steer_start_throws")]
    public void RecordsReceiptAndAttentionWhenSteerStartThrows()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-start-throws");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "start throws")).GetAwaiter().GetResult();
        var attentionStore = new FakeCollaborationItemStore();
        File.WriteAllText(Path.Combine(root, "worker-edit.cs"), "valuable worker edit");

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (_, _, _) => throw new InvalidOperationException("dispatch start failed"),
            prepareFreshDispatch: (k, g, t, _) => k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                "codex-cli",
                "fresh-guided",
                root,
                now.AddSeconds(2),
                "OpenAI",
                AgentCatalog.OpenAiSubscriptionModelAlias,
                WorkerProviderKind: ProviderKind.OpenAICodexCli)),
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("tree-dead", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Contains("dispatch start failed", receipt.Outcome, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.True(string.IsNullOrWhiteSpace(GitCli.Run(root, "status", "--porcelain", "--untracked-files=all").Output));
        Assert.Contains("worker-edit.cs", GitCli.Run(root, "stash", "show", "--include-untracked", "--name-only", "stash@{0}").Output, StringComparison.Ordinal);
        Assert.Matches("preserved=[0-9a-f]{40}", receipt.Outcome);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_requeues_and_preserves_goal_worktree_when_restart_preparation_throws")]
    public void RequeuesAndPreservesGoalWorktreeWhenRestartPreparationThrows()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-prepare-throws");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: null);
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "restart preparation throws")).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root, "worker-edit.cs"), "valuable worker edit");
        var started = false;

        var coordinator = NewCoordinator(
            root,
            store,
            new FakeCollaborationItemStore(),
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("start must not run");
            },
            prepareFreshDispatch: (_, _, _, _) => throw new InvalidOperationException("dispatch preparation failed"),
            currentHead: head);

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState, string.Join(Environment.NewLine, result.ProgressLines));
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Contains("dispatch preparation failed", receipt.Outcome, StringComparison.Ordinal);
        Assert.Matches("preserved=[0-9a-f]{40}", receipt.Outcome);
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.True(string.IsNullOrWhiteSpace(GitCli.Run(root, "status", "--porcelain", "--untracked-files=all").Output));
        Assert.Contains("worker-edit.cs", GitCli.Run(root, "stash", "show", "--include-untracked", "--name-only", "stash@{0}").Output, StringComparison.Ordinal);
    }

    private static ProgressiveReviewSteeringCoordinator NewCoordinator(
        string root,
        InMemoryProgressiveReviewSteeringStore store,
        ICollaborationItemStore? attentionStore = null,
        Func<int, bool>? isProcessRunning = null,
        Func<TaskProcessRecord, IReadOnlyList<int>>? getLineageDescendants = null,
        Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord>? cancelProcess = null,
        Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord>? startProcess = null,
        Action<AgentOrchestratorKernel, Goal, TaskSpec, string>? prepareFreshDispatch = null,
        Action<AgentOrchestratorKernel, Goal, TaskSpec, string, ConductorAutonomyPolicy?>? prepareFreshDispatchWithPolicy = null,
        IReadOnlyList<AgentDefinition>? agents = null,
        ProgressiveReviewSteeringOptions? options = null,
        string? currentHead = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<int, SpawnProcessIdentity?>? readProcessIdentity = null,
        Func<int, DateTimeOffset, IReadOnlyList<int>>? listConservativeLineageDescendants = null)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        return new ProgressiveReviewSteeringCoordinator(
            workspace,
            agents ?? AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(),
            new InMemoryModelProviderRegistry([]),
            store,
            attentionStore ?? new FakeCollaborationItemStore(),
            options,
            utcNow: utcNow ?? (() => new DateTimeOffset(2026, 7, 20, 12, 0, 10, TimeSpan.Zero)),
            isProcessRunning: isProcessRunning ?? (_ => false),
            getLineageDescendants: getLineageDescendants,
            cancelProcess: cancelProcess,
            startProcess: startProcess,
            prepareFreshDispatch: prepareFreshDispatch,
            headResolver: currentHead is null ? null : _ => currentHead,
            capturedHeadIsAncestor: currentHead is null ? null : SameHead,
            prepareFreshDispatchWithPolicy: prepareFreshDispatchWithPolicy,
            readProcessIdentity: readProcessIdentity ?? (processId => TestProcessIdentity(processId)),
            listConservativeLineageDescendants: listConservativeLineageDescendants);
    }

    private static bool SameHead(string _, string? capturedHead, string? currentHead) =>
        string.Equals(capturedHead?.Trim(), currentHead?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord> CancelWithTerminalProof(DateTimeOffset now) =>
        (k, goalId, taskId) =>
        {
            var current = k.GetTask(goalId, taskId).LastProcess!;
            var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
            k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
            WriteExitAndHeartbeat(cancelled, now.AddSeconds(1), childPid: null, ownedPids: [6001], state: "exited");
            return cancelled;
        };

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) RunningDeveloper(
        string root,
        DateTimeOffset dispatchedAt,
        string? worktreeHead,
        string? sessionId,
        TimeSpan? clockOffsetAfterDispatch = null,
        AgentRole role = AgentRole.Developer,
        IReadOnlyList<ReviewFindingLocation>? reviewFindingTouchedAnchors = null,
        string? reviewFindingTouchProofDiagnostic = null,
        ReviewRetryCapReceipt? reviewRetryCap = null,
        WorkerContextPackageReceipt? contextPackageReceipt = null,
        string dispatchedModel = AgentCatalog.OpenAiSolSubscriptionModelAlias)
    {
        Directory.CreateDirectory(Path.Combine(root, ".orchestrator", "logs"));
        var clock = new TestClock(dispatchedAt);
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(new TaskId("developer-task-0001"), "Implement feature.\n\nACCEPTANCE\n- Stay scoped", role);
        var goal = kernel.CreateGoal(new GoalId("goal-progressive-review-0001"), "Progressive review steering goal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec --skip-git-repo-check --sandbox workspace-write -",
            root,
            dispatchedAt,
            "OpenAI",
            dispatchedModel,
            TaskComplexity: TaskComplexity.Complex,
            PromptCharacterCount: 1234,
            PromptPath: Path.Combine(root, ".orchestrator", "prompts", "initial.md"),
            WorkerProviderKind: ProviderKind.OpenAICodexCli,
            ProviderSessionId: sessionId,
            WorktreeHeadSha: worktreeHead,
            DirtyStateHash: "clean",
            ReviewFindingTouchedAnchors: reviewFindingTouchedAnchors,
            ReviewFindingTouchProofDiagnostic: reviewFindingTouchProofDiagnostic,
            ReviewRetryCap: reviewRetryCap,
            ContextPackageReceipt: contextPackageReceipt));
        var catalog = AgentCatalog.Default();
        if (string.Equals(
                dispatchedModel,
                catalog.GetRequired(role).Subscription?.ModelAlias,
                StringComparison.OrdinalIgnoreCase))
        {
            AssertFixtureDispatchMatchesCatalog(nameof(RunningDeveloper), catalog, role, task.LastDispatch!);
        }
        var process = new TaskProcessRecord(
            6001,
            task.LastDispatch!.Command,
            root,
            Path.Combine(root, ".orchestrator", "logs", "out.log"),
            Path.Combine(root, ".orchestrator", "logs", "err.log"),
            Path.Combine(root, ".orchestrator", "logs", "exit.txt"),
            dispatchedAt,
            null,
            null,
            OwnedProcessIds: [6001]);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        WriteHeartbeat(process, dispatchedAt, childPid: null, ownedPids: [process.ProcessId], state: "running");
        if (clockOffsetAfterDispatch is { } offset)
        {
            clock.Advance(offset);
        }
        return (kernel, goal, task);
    }

    private static void AssertFixtureDispatchMatchesCatalog(
        string fixtureName,
        AgentCatalog catalog,
        AgentRole role,
        TaskDispatchRecord dispatch)
    {
        var agent = catalog.GetRequired(role);
        var subscription = agent.Subscription
            ?? throw new InvalidOperationException($"Fixture '{fixtureName}' activates role '{role}', which has no subscription profile.");
        var expectedProvider = WorkerProviderCatalog.Default()
            .ResolveProfile(subscription.WorkerProfileName)
            .Identity.Kind;
        if (dispatch.WorkerProviderKind != expectedProvider ||
            !string.Equals(dispatch.ModelName, subscription.ModelAlias, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Fixture '{fixtureName}' records provider '{dispatch.WorkerProviderKind}' and records model '{dispatch.ModelName}', " +
                $"but catalog role '{role}' resolves provider '{expectedProvider}' and catalog resolves model '{subscription.ModelAlias}'.");
        }
    }

    private static ProgressiveReviewSteerIntent Intent(Goal goal, TaskSpec task, DateTimeOffset now, string guidance) =>
        new(
            Guid.NewGuid().ToString("n"),
            goal.Id.Value,
            task.Id.Value,
            task.RequiredRole.ToString(),
            $"{goal.Id.Value}|{task.Id.Value}|{now.UtcTicks}",
            "glance-abc",
            "inputs-hash",
            now,
            "diff shows wrong subsystem",
            guidance,
            $"ProgressiveReviewSteer guidance. {guidance}",
            now);

    private static string CreateGitRepository(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Assert.True(GitCli.Run(root, "init").Succeeded);
        Assert.True(GitCli.Run(root, "config", "user.email", "test@example.test").Succeeded);
        Assert.True(GitCli.Run(root, "config", "user.name", "Test User").Succeeded);
        File.WriteAllText(Path.Combine(root, "README.md"), "test");
        Assert.True(GitCli.Run(root, "add", "README.md").Succeeded);
        Assert.True(GitCli.Run(root, "commit", "-m", "initial").Succeeded);
        return root;
    }

    private static FileStream LockTextFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
        stream.Position = 0;
        return stream;
    }

    private static void WriteHeartbeat(
        TaskProcessRecord process,
        DateTimeOffset observedAt,
        int? childPid,
        IReadOnlyList<int> ownedPids,
        string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackgroundDispatchRunner.GetHeartbeatPath(process))!);
        var childPidJson = childPid.HasValue
            ? childPid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "null";
        var identityPids = ownedPids
            .Concat(childPid is > 0 ? [childPid.Value] : [])
            .Concat(process.ProcessId > 0 ? [process.ProcessId] : [])
            .Distinct()
            .ToArray();
        var identities = string.Join(",", identityPids.Select(processId =>
            $$"""{"processId":{{processId}},"startedAt":"2026-07-20T11:59:00Z","imagePath":"C:\\workers\\worker-{{processId}}.exe"}"""));
        File.WriteAllText(
            BackgroundDispatchRunner.GetHeartbeatPath(process),
            $$"""
            {"pid":{{process.ProcessId}},"childPid":{{childPidJson}},"ownedPids":[{{string.Join(",", ownedPids)}}],"ownedProcessIdentities":[{{identities}}],"state":"{{state}}","lastObservedAt":"{{observedAt:O}}","lastProgressAt":"{{observedAt:O}}","stdoutBytes":0,"stderrBytes":0,"ownedCpuMs":0}
            """);
    }

    private static SpawnProcessIdentity TestProcessIdentity(int processId) =>
        new(processId, DateTimeOffset.Parse("2026-07-20T11:59:00Z"), $@"C:\workers\worker-{processId}.exe");

    private static void WriteExitAndHeartbeat(
        TaskProcessRecord process,
        DateTimeOffset observedAt,
        int? childPid,
        IReadOnlyList<int> ownedPids,
        string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(process.ExitCodePath)!);
        File.WriteAllText(process.ExitCodePath, "1");
        WriteHeartbeat(process, observedAt, childPid, ownedPids, state);
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }
}
