using System.Reflection;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: in-memory goals, pure command builders and read-only source inspection.
public sealed class NextActionCommandAdviceCharacterizationTests
{
    // Derived from ConsoleViews.Advance.cs at bdd351c7f, before extraction.
    [Theory]
    [InlineData(NextActionKind.AnswerHumanInput, "pending", "pending")]
    [InlineData(NextActionKind.InspectFailedTask, "task 3 | retry 3 <note>", "monitor")]
    [InlineData(NextActionKind.FixFailedVerification, "verifications 3 | retry 3 <note>", "monitor")]
    [InlineData(NextActionKind.RefreshRunningProcess, "refresh-dispatch 3", "monitor")]
    [InlineData(NextActionKind.ExecuteRecordedDispatch, "execute-dispatch 3 --confirm-dispatch-start", "monitor")]
    [InlineData(NextActionKind.VerifyCompletedTask, "verify 3 <command> | verify-manual 3 passed <note>", "monitor")]
    [InlineData(NextActionKind.RunAssignedTask, "run 3", "monitor")]
    [InlineData(NextActionKind.DelegatePendingTask, "delegate", "delegate")]
    [InlineData(NextActionKind.MonitorGoal, "monitor", "monitor")]
    [InlineData((NextActionKind)99, "monitor", "monitor")]
    public void Item_AllKinds_WithAndWithoutTaskNumber_PreserveExactText(
        NextActionKind kind, string withTask, string withoutTask)
    {
        var item = new NextActionItem(kind, null, null, "message");

        Assert.Equal(withTask, NextActionCommandAdvice.BuildSuggestedCommand(item, 3));
        Assert.Equal(withoutTask, NextActionCommandAdvice.BuildSuggestedCommand(item, null));
    }

    // Derived from ConsoleViews.Advance.cs at bdd351c7f, before extraction.
    [Theory]
    [InlineData(false, null, "pending")]
    [InlineData(false, "resume-command", "pending")]
    [InlineData(true, null, "answer abcdef01 <answer>")]
    [InlineData(true, "resume-command", "resume-command")]
    public void HumanInput_RequestAndResumeGuards_PreserveExactText(
        bool hasRequest, string? resumeCommand, string expected)
    {
        var item = new NextActionItem(NextActionKind.AnswerHumanInput, null,
            hasRequest ? new HumanInputRequestId("abcdef0123456789") : null, "message", resumeCommand);

        Assert.Equal(expected, NextActionCommandAdvice.BuildSuggestedCommand(item, 3));
        Assert.Equal(expected, NextActionCommandAdvice.BuildSuggestedCommand(item, null));
    }

    // Derived from ConsoleViews.Advance.cs at bdd351c7f, before extraction.
    [Fact]
    public void Goal_PlainPreparedDispatch_PreservesExactText()
    {
        var (kernel, goal, task, _) = CreateAssignedGoal();
        RecordPlainDispatch(kernel, goal, task);
        Assert.False(SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task)?.IsAnomalous ?? false);

        Assert.Equal("execute-dispatch 1 --confirm-dispatch-start",
            NextActionCommandAdvice.BuildSuggestedCommand(goal, Action(NextActionKind.ExecuteRecordedDispatch, task)));
    }

    // Derived from ConsoleViews.Advance.cs at bdd351c7f, before extraction.
    [Theory]
    [InlineData(true, "task 1")]
    [InlineData(false, "verifications 1 | retry 1 <note>")]
    public void Goal_DirtyAndOrdinaryDispatchFailures_PreserveExactText(bool dirty, string expected)
    {
        var (kernel, goal, task, _) = CreateAssignedGoal();
        RecordPlainDispatch(kernel, goal, task);
        var stderr = dirty
            ? "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
                "branch=goal/abc; head=def; worktree=dirty; commits_after_dispatch=0."
            : "ordinary dispatch failure";
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec", "C:\\repo", 1, string.Empty, stderr, DateTimeOffset.UtcNow));
        Assert.Equal(dirty, DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out _));

        Assert.Equal(expected,
            NextActionCommandAdvice.BuildSuggestedCommand(goal, Action(NextActionKind.FixFailedVerification, task)));
    }

    // Derived from ConsoleViews.Advance.cs at bdd351c7f, before extraction.
    [Fact]
    public void Goal_NullTaskId_PreservesExactText()
    {
        var (_, goal, _, _) = CreateAssignedGoal();

        Assert.Equal("monitor", NextActionCommandAdvice.BuildSuggestedCommand(goal,
            new NextActionItem(NextActionKind.RunAssignedTask, null, null, "message")));
    }

    // Derived from ConsoleViews.Advance.cs at bdd351c7f, before extraction.
    [Fact]
    public void Run_UnresolvedAgents_PreservesExactText()
    {
        var (_, goal, task, agent) = CreateAssignedGoal();
        var action = Action(NextActionKind.RunAssignedTask, task);
        var otherAgent = agent with { Id = new AgentId("other-developer") };

        Assert.Equal("run 1", NextActionCommandAdvice.BuildSuggestedCommand(goal, action));
        Assert.Equal("run 1", NextActionCommandAdvice.BuildSuggestedCommand(goal, action, [otherAgent]));
    }

    // Derived from ConsoleViews.Advance.cs at bdd351c7f, before extraction.
    [Theory]
    [InlineData("OpenAI", AgentExecutionPolicy.PreferSubscription, false, "subscription-dispatch 1")]
    [InlineData("OpenAI", AgentExecutionPolicy.ApiOnly, false, "run 1 --confirm-paid-api-run")]
    [InlineData("OpenAI", AgentExecutionPolicy.ApiOnly, true, "run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt")]
    [InlineData("Local", AgentExecutionPolicy.ApiOnly, false, "run 1")]
    public void Run_ResolvedProviderAndPolicy_PreserveExactText(
        string provider, AgentExecutionPolicy policy, bool largePrompt, string expected)
    {
        var (_, goal, task, agent) = CreateAssignedGoal(provider, policy, largePrompt);
        if (policy == AgentExecutionPolicy.ApiOnly && provider == "OpenAI")
        {
            var risk = ApiPromptCostGuard.Evaluate(AgentTaskRunner.PreviewRun(goal, task, [agent]), goal);
            if (largePrompt)
                Assert.True(Assert.IsType<PaidApiPromptRisk>(risk).PromptExceedsThreshold);
            else
                Assert.Null(risk);
        }

        Assert.Equal(expected,
            NextActionCommandAdvice.BuildSuggestedCommand(goal, Action(NextActionKind.RunAssignedTask, task), [agent]));
    }

    // Derived from ConsoleViews.VerificationCommands.cs at bdd351c7f, before extraction.
    [Theory]
    [InlineData(StageReadinessStatus.ReadyToRun, TaskEvidenceKind.None, "run 1")]
    [InlineData(StageReadinessStatus.InProgress, TaskEvidenceKind.Dispatch, "execute-dispatch 1 --confirm-dispatch-start")]
    [InlineData(StageReadinessStatus.FailedOrCancelled, TaskEvidenceKind.None, "retry 1 <note>")]
    public void GoalStage_RunDispatchAndFallback_PreserveExactText(
        StageReadinessStatus status, TaskEvidenceKind evidence, string expected)
    {
        var (kernel, goal, task, _) = CreateAssignedGoal();
        if (evidence == TaskEvidenceKind.Dispatch)
            RecordPlainDispatch(kernel, goal, task);
        var stage = new TaskStageReadiness(task.Id, task.RequiredRole, task.Description,
            task.Status, true, status, evidence, VerificationGateStatus.NotReady, 0, "message", "suggested action");

        Assert.Equal(expected, NextActionCommandAdvice.BuildStageSuggestedCommand(goal, stage));
    }

    [Fact]
    public void ConsoleViewsRatchet_UsesFamilyTwoAtOrUnderBudget()
    {
        var ceiling = Assert.Single(SourceSizeRatchet.SeededClassCeilings,
            row => row.ClassName == "ConsoleViews");
        Assert.True(ceiling.MaximumTotalLineCount <= 2930);
        Assert.Equal(38, ceiling.MaximumPartialFileCount);
        Assert.Empty(SourceSizeRatchet.EvaluateClasses(VerifiedRepositoryRoot.Find(), [ceiling]));
    }

    [Fact]
    public void ExtractedAdvice_OwnsBuildersWithoutConsoleViewsForwarders()
    {
        var root = VerifiedRepositoryRoot.Find();
        var advicePath = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Cli", "NextActionCommandAdvice.cs");
        Assert.True(File.Exists(advicePath), "NextActionCommandAdvice.cs must own the extracted builders.");
        var advice = File.ReadAllText(advicePath);
        Assert.Contains("internal static class NextActionCommandAdvice", advice);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", advice);

        var oldMethods = typeof(ConsoleViews).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        foreach (var name in new[] { "BuildSuggestedCommand", "BuildStageSuggestedCommand",
                     "BuildRunAssignedTaskCommand", "ResolveAssignedAgent" })
            Assert.DoesNotContain(oldMethods, method => method.Name == name);

        // Match the ratchet's declaration scan, including ConsoleViews.cs and excluding build outputs.
        var partials = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Select(File.ReadAllText)
            .Where(text => Regex.IsMatch(text, @"\bpartial\s+class\s+ConsoleViews\b", RegexOptions.CultureInvariant))
            .ToArray();
        Assert.Equal(38, partials.Length);
        foreach (var text in partials)
        {
            Assert.DoesNotMatch(@"\bstatic\s+\S+\s+(?:BuildSuggestedCommand|BuildStageSuggestedCommand|BuildRunAssignedTaskCommand|ResolveAssignedAgent)\s*\(", text);
            Assert.DoesNotMatch(@"(?:=>|\breturn)\s*NextActionCommandAdvice\.", text);
        }
        // Five print-method calls remain; none is a forwarding member.
        Assert.Equal(5, partials.Sum(text => Regex.Matches(text, @"\bNextActionCommandAdvice\.").Count));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, AgentDefinition Agent)
        CreateAssignedGoal(string provider = "OpenAI",
            AgentExecutionPolicy policy = AgentExecutionPolicy.ApiOnly, bool largePrompt = false)
    {
        var kernel = new AgentOrchestratorKernel();
        // Match the existing large paid API fixture: preview trims each context block separately.
        var goal = kernel.CreateGoal(
            largePrompt
                ? "production architecture api cli dashboard provider subscription worker persistence state tests docs " + new string('o', 5000)
                : "Do work",
            [new TaskSpec(TaskId.New(),
                largePrompt
                    ? "Design and implement complete integration with authentication migration rollback state persistence and dashboard api tests. " + new string('d', 5000)
                    : "Do work",
                AgentRole.Developer,
                largePrompt
                    ? "Run end-to-end integration tests, dashboard smoke tests, api tests, cli tests, and rollback checks. " + new string('v', 5000)
                    : null)]);
        var agent = new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer,
            new ModelProfile(provider, "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: policy,
            Subscription: policy == AgentExecutionPolicy.PreferSubscription ? new SubscriptionLaunchProfile("codex-cli") : null);
        kernel.ActivateGoal(goal.Id, [agent]);
        return (kernel, goal, goal.Tasks.Single(), agent);
    }

    private static NextActionItem Action(NextActionKind kind, TaskSpec task) =>
        new(kind, task.Id, null, "message");

    private static void RecordPlainDispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task) =>
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", DateTimeOffset.UtcNow,
            "OpenAI", "gpt-5-codex", "medium", TaskComplexity.Simple, 100));
}
