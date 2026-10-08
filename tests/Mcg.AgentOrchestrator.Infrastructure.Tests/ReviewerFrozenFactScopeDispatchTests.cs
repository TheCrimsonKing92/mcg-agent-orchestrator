using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ReviewerFrozenFactScopeDispatchTests : WorkerDispatchTestSupport
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReviewerPreparationIncludesViolationAndEmitsExactlyOneConductEvent()
    {
        var root = CreateSeededDispatchRepository();
        const string path = "tests/Feature.Tests/FrozenTests.cs";
        var baselineFile = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(baselineFile)!);
        const string before = "class FrozenTests { [Fact] public void Kept() { Assert.True(true); } [Fact] public void Edited() { Assert.Equal(1, 1); } }";
        File.WriteAllText(baselineFile, before);
        RunGit(root, ["add", path], Timestamp);
        RunGit(root, ["commit", "-m", "Seed frozen facts"], Timestamp);
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var reviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Review frozen fact scope", [reviewer]);
        var agent = new AgentDefinition(new AgentId("reviewer"), "Reviewer", AgentRole.Reviewer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, reviewer.Id, "Allow Kept amendment", HumanWaitKind.SpecClarification).Request;
        kernel.SubmitHumanInput(request.Id, new FrozenFactRuling(["FrozenTests"],
            [new("FrozenTests.Kept", path, "Change only Kept")], "New contract", "Edited stays", "Inspect diff", ["receipt"]).Render());
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        WriteSkill(worktree, "orchestrator-worker-verification");
        File.WriteAllText(Path.Combine(worktree, path), before.Replace("Assert.Equal(1, 1)", "Assert.Equal(2, 2)", StringComparison.Ordinal));
        RunGit(worktree, ["add", path], Timestamp);
        RunGit(worktree, ["commit", "-m", "Edit frozen fact"], Timestamp);
        var promptRoot = Path.Combine(root, "prompts");
        var result = WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, goal, reviewer, [agent], DispatchTestProfiles(),
            promptRoot, worktree, Timestamp,
            sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget),
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli,
            commandExists: DispatcherProviderProbeFakes.ProviderCommandsPresent);
        var prompt = File.ReadAllText(result.PromptPath);
        Assert.Contains("## Changed existing tests", prompt);
        Assert.Contains($"- FrozenTests.Edited ({path}:1-1) changed: frozen-class violation {request.Id.Value}", prompt);
        var logPath = Path.Combine(root, "logs", "conduct-events.log");
        Assert.True(File.Exists(logPath), "Reviewer preparation must write the conduct event artifact.");
        var events = File.ReadAllLines(logPath).Where(line => line.Contains("FROZEN_FACT_SCOPE", StringComparison.Ordinal)).ToArray();
        var line = Assert.Single(events);
        using var document = JsonDocument.Parse(line);
        Assert.Equal("frozen-fact-scope", document.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal(goal.Id.Value, document.RootElement.GetProperty("goalId").GetString());
        Assert.Equal(Timestamp, document.RootElement.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Equal($"FROZEN_FACT_SCOPE goal={goal.Id.Value[..8]} task=1 violations=1 facts=FrozenTests.Edited",
            document.RootElement.GetProperty("detail").GetString());

        // The refresh-before-start path reuses this dispatch; it must not emit another event.
        var scope = ReviewerChangedExistingTestScope.Read(kernel, goal, reviewer, worktree,
            GitCli.Run(worktree, "merge-base", "main", "HEAD").Output.Trim(),
            GitCli.Run(worktree, "rev-parse", "HEAD").Output.Trim(), [path]);
        Assert.Equal(ChangedExistingTestClass.FrozenClassViolation, Assert.Single(scope.Entries).Class);
        ReviewerChangedExistingTestScope.RecordConductEvent(goal, reviewer, scope, promptRoot, Timestamp, true);
        Assert.Single(File.ReadAllLines(logPath).Where(item => item.Contains("FROZEN_FACT_SCOPE", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void OtherRolesDoNotReadGitOrEmitScopeEvents(AgentRole role)
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var task = new TaskSpec(TaskId.New(), "Implement or test", role);
        var goal = kernel.CreateGoal("Other role", [task]);
        var read = ReviewerChangedExistingTestScope.Read(kernel, goal, task, "directory-does-not-exist",
            "missing", "missing", ["tests/A.cs"]);
        Assert.Empty(read.Entries);
        Assert.Null(read.Diagnostic);
        ReviewerChangedExistingTestScope.RecordConductEvent(goal, task, read, Path.Combine(root, "prompts"), Timestamp, false);
        Assert.False(File.Exists(Path.Combine(root, "logs", "conduct-events.log")));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Timestamp;
    }
}
