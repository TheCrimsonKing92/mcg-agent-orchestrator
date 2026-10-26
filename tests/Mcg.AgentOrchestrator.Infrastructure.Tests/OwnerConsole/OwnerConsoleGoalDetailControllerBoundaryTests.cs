using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test kernel and fakes; source reads use the verified repository root.
public sealed class OwnerConsoleGoalDetailControllerBoundaryTests
{
    [Fact]
    public async Task ShowGoalDetail_InjectedQuery_MatchesExistingFormatterText()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.Kernel.CreateGoal(new GoalId("11111111-detail"), "# Readable detail\nLong objective",
            [new(TaskId.New(), "Build", AgentRole.Developer), new(TaskId.New(), "Review", AgentRole.Reviewer)]);
        harness.Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with
        {
            CurrentHold = new GoalHoldSnapshot("hold", "owner-review-hold",
                "waiting_for_approval {metadata} evidence=[raw]", harness.Clock.GetUtcNow())
        }).ToArray() });
        goal = harness.Kernel.Goals.Single();
        var recordedTail = new RecordedTail(Enumerable.Range(0, 12).Select(index => JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index), eventType = "TaskDispatched",
            taskId = goal.Tasks[0].Id.Value, message = "{\"eventKind\":\"secret\"}",
            detail = new { secret = "unreadable" }
        })).ToArray());
        var throwingState = new ThrowingState();
        var throwingTail = new ThrowingTail();
        var dialogs = new Dialogs();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            throwingState, throwingTail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            goalDetail: new OwnerGoalDetailQuery(harness.State, recordedTail));

        await controller.ShowGoalDetailAsync(goal.Id.Value, cancellationToken: TestContext.Current.CancellationToken);

        var output = new OwnerConsoleHarness.FakeOutput();
        await OwnerConsoleGoalDetailFormatter.ComposeAsync(harness.State, recordedTail, goal.Id.Value,
            output, TestContext.Current.CancellationToken, harness.Clock.LocalTimeZone);
        var expected = string.Join(Environment.NewLine, output.Text.Replace("\r", "").TrimEnd('\n').Split('\n'));
        var text = Assert.Single(dialogs.Texts);
        Assert.Equal(expected, text);
        Assert.Contains("Status:", text);
        Assert.Contains("Tasks:", text);
        Assert.Contains("Recent events:", text);
        Assert.Contains("Waiting on: waiting for your approval", text);
        Assert.Contains("Developer started", text);
        Assert.Equal(0, throwingState.Calls);
        Assert.Equal(0, throwingTail.Calls);
    }

    [Fact]
    public async Task ShowGoalDetail_UnknownGoal_ShowsUnavailableWithoutReadingRawStores()
    {
        var harness = new OwnerConsoleHarness();
        var throwingState = new ThrowingState();
        var throwingTail = new ThrowingTail();
        var dialogs = new Dialogs();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            throwingState, throwingTail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            goalDetail: new OwnerGoalDetailQuery(harness.State, new RecordedTail([])));

        await controller.ShowGoalDetailAsync("unknown-goal", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("goal state unavailable", Assert.Single(dialogs.Texts));
        Assert.Equal(0, throwingState.Calls);
        Assert.Equal(0, throwingTail.Calls);
    }

    [Fact]
    public void ComposeGoal_Source_UsesOnlyQueryForDetailReads()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "src",
            "Mcg.AgentOrchestrator.App", "Console", "OwnerConsoleScreenController.cs"));
        var method = ExtractComposeGoal(source);

        Assert.Empty(DetailReadViolations(method));
        Assert.DoesNotContain("ComposeAsync(state, tail", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailReadGuard_OldStoreCall_ReportsBothRawStores()
    {
        var violations = DetailReadViolations(
            "OwnerConsoleGoalDetailFormatter.ComposeAsync(state, tail, goalId, output, cancellation, zone)");

        Assert.Contains("state", violations);
        Assert.Contains("tail", violations);
    }

    [Fact]
    public async Task RunGoalCommand_PrefixAndMissing_MatchesResolverTextWithoutRawStores()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("3333333a-first", "First match", AgentRole.Developer);
        harness.AddGoal("3333333b-second", "Second match", AgentRole.Developer);
        var throwingState = new ThrowingState();
        var throwingTail = new ThrowingTail();
        var recordedTail = new RecordedTail([]);
        var dialogs = new Dialogs();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            throwingState, throwingTail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            goalDetail: new OwnerGoalDetailQuery(harness.State, recordedTail));

        var baselineAmbiguous = new OwnerConsoleCapturedOutput();
        await OwnerConsoleGoalDetail.ResolveAsync(harness.State, "3333333", baselineAmbiguous,
            TestContext.Current.CancellationToken);
        await controller.RunCommandAsync(":goal 3333333", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Command result", Assert.Single(dialogs.Titles));
        Assert.Equal(baselineAmbiguous.Text, Assert.Single(dialogs.Texts));

        var baselineMissing = new OwnerConsoleCapturedOutput();
        await OwnerConsoleGoalDetail.ResolveAsync(harness.State, "missing", baselineMissing,
            TestContext.Current.CancellationToken);
        await controller.RunCommandAsync(":goal missing", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Command result", "Command result" }, dialogs.Titles);
        Assert.Equal(new[] { baselineAmbiguous.Text, baselineMissing.Text }, dialogs.Texts);
        Assert.Equal(0, throwingState.Calls);
        Assert.Equal(0, throwingTail.Calls);
    }

    [Fact]
    public async Task RunGoalCommand_FullId_MatchesFormatterTextWithoutRawStores()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("44444444-detail", "Readable detail", AgentRole.Developer);
        harness.AddGoal("44444444-detail-extra", "Other goal", AgentRole.Developer);
        var recordedTail = new RecordedTail([JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UnixEpoch, eventType = "TaskDispatched",
            taskId = goal.Tasks[0].Id.Value
        })]);
        var throwingState = new ThrowingState();
        var throwingTail = new ThrowingTail();
        var dialogs = new Dialogs();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            throwingState, throwingTail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            goalDetail: new OwnerGoalDetailQuery(harness.State, recordedTail));

        await controller.RunCommandAsync(":goal " + goal.Id.Value,
            cancellationToken: TestContext.Current.CancellationToken);

        var output = new OwnerConsoleHarness.FakeOutput();
        await OwnerConsoleGoalDetailFormatter.ComposeAsync(harness.State, recordedTail, goal.Id.Value,
            output, TestContext.Current.CancellationToken, harness.Clock.LocalTimeZone);
        var expected = string.Join(Environment.NewLine, output.Text.Replace("\r", "").TrimEnd('\n').Split('\n'));
        Assert.Equal(expected, Assert.Single(dialogs.Texts));
        Assert.Equal(0, throwingState.Calls);
        Assert.Equal(0, throwingTail.Calls);
    }

    [Fact]
    public void RunCommand_Source_ReadsStoresOnlyInConstructorAndQueryInitializer()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "src",
            "Mcg.AgentOrchestrator.App", "Console", "OwnerConsoleScreenController.cs"));

        Assert.Empty(ControllerStoreReadViolations(source));
        Assert.DoesNotContain("ResolveAsync(state", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerStoreGuard_OldResolveCall_ReportsState()
    {
        const string oldCall = "goalId = await OwnerConsoleGoalDetail.ResolveAsync(state, parts[1], output, stepToken);";
        const string source = """
            internal sealed class Controller(IOrchestratorStateQueries state, IGoalEventTail tail)
            {
                private readonly OwnerGoalDetailQuery _goalDetail = new OwnerGoalDetailQuery(state, tail);
                async Task ExecuteAsync()
                {
                    var text = "goal state unavailable";
                    goalId = await OwnerConsoleGoalDetail.ResolveAsync(state, parts[1], output, stepToken);
                }
            }
            """;

        Assert.Contains("state", ControllerStoreReadViolations(source));
        Assert.Empty(ControllerStoreReadViolations(source.Replace(oldCall,
            "goalId = await _goalDetail.ResolveGoalIdAsync(parts[1], output, stepToken);", StringComparison.Ordinal)));
    }

    private static IReadOnlyList<string> ControllerStoreReadViolations(string source)
    {
        var stripped = Regex.Replace(source, "\"(?:[^\"\\\\\r\n]|\\\\.)*\"", "\"\"");
        var constructor = Regex.Match(stripped, @"\bclass\s+\w+\s*\(");
        if (!constructor.Success) return ["missing primary constructor"];
        var start = constructor.Index + constructor.Length - 1;
        var end = start;
        var depth = 0;
        for (; end < stripped.Length; end++)
        {
            if (stripped[end] == '(') depth++;
            else if (stripped[end] == ')' && --depth == 0) break;
        }
        if (end == stripped.Length) return ["missing primary constructor end"];
        stripped = stripped.Remove(start, end - start + 1);
        var initializer = Regex.Match(stripped, @"(?m)^[^\r\n]*\b_goalDetail\s*=[^\r\n]*");
        if (!initializer.Success) return ["missing _goalDetail initializer"];
        stripped = stripped.Remove(initializer.Index, initializer.Length);
        return Regex.Matches(stripped, @"\b(?:state|tail)\b").Select(match => match.Value).ToArray();
    }

    private static string ExtractComposeGoal(string source)
    {
        const string signature = "private async Task<OwnerConsoleGoalDialog.Content?> ComposeGoalAsync(";
        const string nextMethod = "private async Task AnswerAsync";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "ComposeGoalAsync signature was not found.");
        var end = source.IndexOf(nextMethod, start, StringComparison.Ordinal);
        Assert.True(end > start, "AnswerAsync anchor after ComposeGoalAsync was not found.");
        return source[start..end];
    }

    private static IReadOnlyList<string> DetailReadViolations(string method)
    {
        var violations = new List<string>();
        if (!method.Contains("_goalDetail", StringComparison.Ordinal)) violations.Add("missing _goalDetail");
        // ComposeGoalAsync has no literals or comments containing these whole-word identifiers.
        foreach (var identifier in new[] { "state", "tail" })
            if (Regex.IsMatch(method, $@"\b{identifier}\b")) violations.Add(identifier);
        foreach (var member in new[] { "LoadGoalsAsync", "ListGoalMetadataAsync", "ReadLast(" })
            if (method.Contains(member, StringComparison.Ordinal)) violations.Add(member);
        return violations;
    }

    private sealed class ThrowingState : IOrchestratorStateQueries
    {
        internal int Calls;
        private InvalidOperationException UnexpectedRead()
        {
            Calls++;
            return new InvalidOperationException("Goal detail must use the injected query.");
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) => throw UnexpectedRead();
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(
            CancellationToken cancellationToken = default) => throw UnexpectedRead();
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(bool includeTerminalCreatedAt,
            CancellationToken cancellationToken = default) => throw UnexpectedRead();
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) => throw UnexpectedRead();
        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) => throw UnexpectedRead();
        public Task<IReadOnlyList<GoalSummary>> ListGoalIdStatusesAsync(
            CancellationToken cancellationToken = default) => throw UnexpectedRead();
    }

    private sealed class ThrowingTail : IGoalEventTail
    {
        internal int Calls;
        public IReadOnlyList<string> ReadLast(string id, int count)
        {
            Calls++;
            throw new InvalidOperationException("Goal detail must use the injected query.");
        }
    }

    private sealed class RecordedTail(string[] lines) : IGoalEventTail
    {
        public IReadOnlyList<string> ReadLast(string id, int count) => lines.TakeLast(count).ToArray();
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        internal readonly List<string> Titles = [];
        public Task ShowTextAsync(string title, string text) { Titles.Add(title); Texts.Add(text); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException();
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException();
    }
}
