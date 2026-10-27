using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private stores/child environments; console capture uses AsyncLocal routing.
public sealed class CliAskGoalCommandTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RealCliAsksOnlySelectedGoal(bool explicitGoal, bool separateWords)
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var before = await fixture.SnapshotAsync();
        var outbox = fixture.OutboxRows();
        var question = "Which release should proceed?";
        string[] words = separateWords ? ["Which", "release", "should", "proceed?"] : [question];
        string[] args = explicitGoal ? ["ask-goal", "--goal", "ABC20000", .. words] : ["ask-goal", .. words];

        var result = await fixture.RunAsync(args);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("Human input requested:", result.StandardOutput);
        var targetId = explicitGoal ? CliAskGoalFixture.OtherId.Value : CliAskGoalFixture.CurrentId.Value;
        Assert.Contains(targetId, result.StandardOutput);
        var after = await fixture.SnapshotAsync();
        AssertOneTargetedRequest(before, after, targetId, question);
        Assert.Equal(outbox, fixture.OutboxRows());
    }

    public static IEnumerable<object[]> InvalidInvocations()
    {
        yield return [new[] { "ask-goal" }, "Provide a question"];
        yield return [new[] { "ask-goal", "   " }, "Provide a question"];
        yield return [new[] { "ask-goal", "--goal" }, "--goal requires"];
        yield return [new[] { "ask-goal", "--goal", "", "Question?" }, "--goal requires"];
        yield return [new[] { "ask-goal", "--goal", "   ", "Question?" }, "--goal requires"];
        yield return [new[] { "ask-goal", "--goal", "abc20000" }, "Provide a question"];
        yield return [new[] { "ask-goal", "--goal", "abc20000", "  " }, "Provide a question"];
        yield return [new[] { "ask-goal", "--goal", "missing", "Question?" }, "was not found"];
        yield return [new[] { "ask-goal", "--goal", "abc", "Question?" }, "ambiguous"];
        yield return [new[] { "ask-goal", "--goal", "abc20000", "--goal", "abc10000", "Question?" }, "only once"];
        yield return [new[] { "ask-goal", "--goal=abc20000", "Question?" }, "Unknown option"];
        yield return [new[] { "ask-goal", "--unsupported", "Question?" }, "Unknown option"];
        yield return [new[] { "ask-goal", "-x", "Question?" }, "Unknown option"];
        yield return [new[] { "ask-goal", "Question?", "--unsupported" }, "Unknown option"];
        yield return [new[] { "ask-goal", "--goal", "abc20000", "Question?", "--unsupported" }, "Unknown option"];
        yield return [new[] { "ask-goal", "--", "Question?" }, "Unknown option"];
    }

    [Theory]
    [MemberData(nameof(InvalidInvocations))]
    public async Task RealCliRejectsInvalidInputWithoutChangingAnyRow(string[] args, string diagnostic)
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var before = fixture.StateRows();

        // Both syntactic and resolution errors must precede mutation-path configuration.
        var result = await fixture.RunAsync(args, poisonCleanup: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(diagnostic, result.StandardError);
        Assert.Contains(CliCommandHelp.AskGoalUsage, result.StandardError);
        Assert.DoesNotContain("Human input requested:", result.StandardOutput);
        Assert.Equal(before, fixture.StateRows());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingStoreIsNotCreatedForRejectedInvocation(bool explicitGoal)
    {
        using var fixture = new CliAskGoalFixture();
        var result = await fixture.RunAsync(explicitGoal
            ? ["ask-goal", "--goal", "abc20000", "Question?"] : ["ask-goal", "Question?"], poisonCleanup: true);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(explicitGoal ? "was not found" : "Create a goal first", result.StandardError);
        Assert.Contains(CliCommandHelp.AskGoalUsage, result.StandardError);
        Assert.False(Directory.Exists(fixture.Workspace.OrchestratorDirectory));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task PersistentAndReplRoutesPreserveCurrentSelection(
        bool hasCurrent, bool explicitGoal, bool repl)
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var kernel = await fixture.Repository.LoadAsync();
        var before = kernel.ExportSnapshot();
        Goal? current = hasCurrent ? kernel.GetGoal(CliAskGoalFixture.CurrentId) : null;
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        const string question = "Which release should proceed?";
        IReadOnlyList<string> args = repl
            ? CliArgumentParser.SplitCommand(explicitGoal
                ? "ask-goal --goal abc20000 \"Which release should proceed?\""
                : "ask-goal Which release should proceed?")
            : CliArgumentParser.NormalizeArgs(["ask-goal", "--goal", "abc20000", question]);
        var outbox = fixture.OutboxRows();
        var changed = false;

        AsyncLocalConsoleRouter.Capture(() => changed = CliPersistentStateRunner.ExecuteCommand(
            args, fixture.Repository, fixture.Workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current));

        Assert.True(changed);
        Assert.Equal(hasCurrent ? CliAskGoalFixture.CurrentId : null, current?.Id);
        AssertOneTargetedRequest(before, await fixture.SnapshotAsync(),
            (explicitGoal ? CliAskGoalFixture.OtherId : CliAskGoalFixture.CurrentId).Value, question);
        Assert.Equal(outbox, fixture.OutboxRows());
    }

    [Theory]
    [MemberData(nameof(InvalidInvocations))]
    public async Task PersistentRejectionPreservesSelectionAndOutboxes(string[] args, string diagnostic)
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var kernel = await fixture.Repository.LoadAsync();
        Goal? current = kernel.GetGoal(CliAskGoalFixture.CurrentId);
        var originalCurrent = current;
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var before = fixture.StateRows();

        var error = Assert.Throws<ArgumentException>(() => CliPersistentStateRunner.ExecuteCommand(
            CliArgumentParser.NormalizeArgs(args), fixture.Repository, fixture.Workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current));

        Assert.Contains(diagnostic, error.Message);
        Assert.Contains(CliCommandHelp.AskGoalUsage, error.Message);
        Assert.Same(originalCurrent, current);
        Assert.Equal(before, fixture.StateRows());
    }

    [Theory]
    [InlineData("ask-goal --goal \"\" abc20000 Which release?", "--goal requires")]
    [InlineData("ask-goal Which release? --goal \"\"", "--goal requires")]
    [InlineData("ask-goal --goal abc20000 \"\"", "Provide a question")]
    public async Task ReplEmptyArgumentsAreRejectedWithoutChangingAnyRow(string line, string diagnostic)
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var kernel = await fixture.Repository.LoadAsync();
        Goal? current = kernel.GetGoal(CliAskGoalFixture.CurrentId);
        var originalCurrent = current;
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var before = fixture.StateRows();

        var error = Assert.Throws<ArgumentException>(() => CliPersistentStateRunner.ExecuteCommand(
            CliArgumentParser.SplitCommand(line), fixture.Repository, fixture.Workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current));

        Assert.Contains(diagnostic, error.Message);
        Assert.Contains(CliCommandHelp.AskGoalUsage, error.Message);
        Assert.Same(originalCurrent, current);
        Assert.Equal(before, fixture.StateRows());
    }

    [Theory]
    [InlineData("ask-goal --goal abc20000 Which release?", "Which release?")]
    [InlineData("ask-goal Which release? --goal abc20000", "Which release?")]
    [InlineData("ask-goal --goal abc20000 \"Which \\\"release\\\"?\"", "Which \"release\"?")]
    public async Task DirectReplDispatchUsesSharedQuotingAndSelectorRules(string line, string question)
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var kernel = await fixture.Repository.LoadAsync();
        var before = kernel.ExportSnapshot();
        Goal? current = kernel.GetGoal(CliAskGoalFixture.CurrentId);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var changed = false;
        AsyncLocalConsoleRouter.Capture(() => changed = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand(line), kernel, fixture.Workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current));
        Assert.True(changed);
        Assert.Equal(CliAskGoalFixture.CurrentId, current?.Id);
        AssertOneTargetedRequest(before, kernel.ExportSnapshot(), CliAskGoalFixture.OtherId.Value, question);
    }

    [Fact]
    public async Task LegacyWithoutCurrentDoesNotFallBackToLatestGoal()
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var before = fixture.StateRows();
        Goal? current = null;
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var error = Assert.Throws<ArgumentException>(() => CliPersistentStateRunner.ExecuteCommand(
            ["ask-goal", "Question?"], fixture.Repository, fixture.Workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current));
        Assert.Contains("Create a goal first", error.Message);
        Assert.Null(current);
        Assert.Equal(before, fixture.StateRows());
    }

    [Fact]
    public async Task RepeatedQuestionRetainsExistingDeduplication()
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var result = await fixture.RunAsync(["ask-goal", "--goal", "abc20000", "Same question?"]);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        var snapshot = await fixture.SnapshotAsync();
        Assert.Single(snapshot.HumanInputRequests.Where(request => request.GoalId == CliAskGoalFixture.OtherId.Value));
        Assert.Equal(GoalStatus.WaitingForHuman, snapshot.Goals.Single(goal => goal.Id == CliAskGoalFixture.OtherId.Value).Status);
    }

    [Fact]
    public async Task TerminalGoalRetainsExistingLifecycleRejection()
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var snapshot = await fixture.SnapshotAsync();
        await fixture.Repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == CliAskGoalFixture.OtherId.Value
                ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
        }));
        var before = fixture.StateRows();
        var result = await fixture.RunAsync(["ask-goal", "--goal", "abc20000", "Question?"]);
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("Human input requested:", result.StandardOutput);
        Assert.Equal(before, fixture.StateRows());
    }

    private static void AssertOneTargetedRequest(
        OrchestratorSnapshot before, OrchestratorSnapshot after, string targetId, string question)
    {
        var added = Assert.Single(after.HumanInputRequests.Where(request =>
            before.HumanInputRequests.All(existing => existing.Id != request.Id)));
        Assert.Equal(targetId, added.GoalId);
        Assert.Null(added.TaskId);
        Assert.Equal(question, added.Question);
        Assert.Equal(before.HumanInputRequests.Count + 1, after.HumanInputRequests.Count);
        Assert.Equal(JsonSerializer.Serialize(before.HumanInputRequests),
            JsonSerializer.Serialize(after.HumanInputRequests.Where(request => request.Id != added.Id).ToArray()));
        Assert.Equal(before.Goals.Count, after.Goals.Count);
        foreach (var goal in before.Goals)
        {
            var next = Assert.Single(after.Goals.Where(candidate => candidate.Id == goal.Id));
            if (goal.Id == targetId)
            {
                Assert.Equal(GoalStatus.WaitingForHuman, next.Status);
                Assert.Equal(JsonSerializer.Serialize(goal.Tasks), JsonSerializer.Serialize(next.Tasks));
            }
            else Assert.Equal(JsonSerializer.Serialize(goal), JsonSerializer.Serialize(next));
        }
    }
}
