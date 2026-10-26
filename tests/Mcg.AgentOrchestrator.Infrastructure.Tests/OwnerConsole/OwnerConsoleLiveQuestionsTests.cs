using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its kernel and its uniquely named temporary directory.
public sealed class OwnerConsoleLiveQuestionsTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MixedQuestions_HidesStaleWaitsAndDuplicatesWithReasonsAndHeaderCounts()
    {
        var harness = new OwnerConsoleHarness();
        var cancelled = harness.AddGoal("11111111111111111111111111111111", "Cancelled", AgentRole.Developer);
        var completed = harness.AddGoal("22222222222222222222222222222222", "Completed", AgentRole.Developer);
        var superseded = harness.AddGoal("33333333333333333333333333333333", "Superseded", AgentRole.Developer);
        var active = harness.AddGoal("44444444444444444444444444444444", "Active", AgentRole.Developer);
        using var directory = new QuestionDirectory();
        var store = CollaborationItemStore.ForDirectory(directory.Path);
        var missing = await store.RaiseAsync(CollaborationItemType.Clarification,
            "55555555555555555555555555555555", "Missing goal question", "Question: Missing goal question",
            "spec-clarification:missing");
        var duplicate = await store.RaiseAsync(CollaborationItemType.Clarification,
            active.Id.Value, "Duplicate", "Question: May this touch billing?", "spec-clarification:duplicate");
        var requests = new[]
        {
            Request(cancelled.Id.Value, "Cancelled question"),
            Request(completed.Id.Value, "Completed question"),
            Request(superseded.Id.Value, "Superseded question"),
            Request(active.Id.Value, "Prospective evidence", HumanWaitKind.ProspectiveAcceptanceEvidence),
            Request(active.Id.Value, "May this touch billing?") with
            {
                RequestedAt = duplicate.RaisedAt.AddMinutes(-1), SuggestedDefaultAnswer = "yes"
            }
        };
        var before = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(before with
        {
            Goals = before.Goals.Select(goal => goal with
            {
                Status = goal.Id == cancelled.Id.Value ? GoalStatus.Cancelled :
                    goal.Id == completed.Id.Value ? GoalStatus.Completed :
                    goal.Id == superseded.Id.Value ? GoalStatus.Superseded : GoalStatus.Active
            }).ToArray(),
            HumanInputRequests = requests
        });
        Assert.All(harness.Kernel.HumanInputRequests, request => Assert.False(request.IsCompleted));
        var source = new OwnerQuestionReadModel(harness.State, directory.Path);

        var snapshot = await source.ReadAsync(CancellationToken.None);

        var live = Assert.Single(snapshot.Live);
        Assert.Equal(requests[4].Id, live.ItemId);
        Assert.Equal("May this touch billing?", live.Text);
        Assert.Equal("yes", live.ProposedDefault);
        Assert.Equal(new Dictionary<string, string>
        {
            [requests[0].Id] = "goal-cancelled",
            [requests[1].Id] = "goal-completed",
            [requests[2].Id] = "goal-superseded",
            [requests[3].Id] = "non-blocking-kind",
            [missing.Id] = "goal-missing",
            [duplicate.Id] = "duplicate"
        }, snapshot.Hidden.ToDictionary(item => item.Question.ItemId, item => item.Reason));
        Assert.Equal(snapshot.Live, await source.ListOpenAsync(CancellationToken.None));
        var session = new OwnerConsoleSession(harness.State, source, harness.Answers,
            harness.Liveness, harness.Digest, harness.Tail, harness.Output, harness.Clock);

        await session.StartAsync(null, CancellationToken.None);
        await session.HandleCommandAsync("accept 1", CancellationToken.None);
        await session.HandleCommandAsync("answer 1 reviewed", CancellationToken.None);

        Assert.Contains("owner questions: 1 | hidden: 6", harness.Output.Text);
        Assert.Contains("[1] 44444444 May this touch billing? | default: yes", harness.Output.Text);
        Assert.Equal(1, harness.Output.Text.Count(character => character == '\a'));
        Assert.DoesNotContain("[2]", harness.Output.Text);
        Assert.DoesNotContain("Cancelled question", harness.Output.Text);
        Assert.DoesNotContain("Completed question", harness.Output.Text);
        Assert.DoesNotContain("Superseded question", harness.Output.Text);
        Assert.DoesNotContain("Missing goal question", harness.Output.Text);
        Assert.DoesNotContain("Prospective evidence", harness.Output.Text);
        Assert.Equal([(requests[4].Id, "yes"), (requests[4].Id, "reviewed")], harness.Answers.Calls);
        Assert.All(harness.Kernel.HumanInputRequests, request => Assert.False(request.IsCompleted));
        var stored = await store.GetAttentionQueueAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, item => Assert.Equal(CollaborationItemStatus.Raised, item.Status));
        Assert.Equal(missing.Body, stored.Single(item => item.Id == missing.Id).Body);
        Assert.Equal(duplicate.Body, stored.Single(item => item.Id == duplicate.Id).Body);
    }

    [Theory]
    [InlineData(GoalStatus.Completed, "goal-completed")]
    [InlineData(GoalStatus.Cancelled, "goal-cancelled")]
    [InlineData(GoalStatus.Superseded, "goal-superseded")]
    [InlineData(GoalStatus.Active, null)]
    [InlineData(GoalStatus.Verified, null)]
    [InlineData(GoalStatus.Failed, null)]
    [InlineData(GoalStatus.Parked, null)]
    public async Task ClarificationsAndStewardHolds_FilterOnlyTheSpecifiedTerminalGoals(
        GoalStatus status, string? expectedReason)
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111111111111111111111111111", "Question goal", AgentRole.Developer);
        var before = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(before with
        {
            Goals = [before.Goals.Single() with
            {
                Status = status,
                CurrentHold = new GoalHoldSnapshot("steward-1", "steward-owner-question",
                    "question=Should this be retried? evidence=[receipt]", RequestedAt)
            }]
        });
        using var directory = new QuestionDirectory();
        var store = CollaborationItemStore.ForDirectory(directory.Path);
        var clarification = await store.RaiseAsync(CollaborationItemType.Clarification,
            goal.Id.Value.ToUpperInvariant(), "Clarification", "Question: Is this feasible?",
            "spec-clarification:status");

        var snapshot = await new OwnerQuestionReadModel(harness.State, directory.Path)
            .ReadAsync(CancellationToken.None);

        if (expectedReason is null)
        {
            Assert.Empty(snapshot.Hidden);
            Assert.Equal(["steward-1", clarification.Id], snapshot.Live.Select(question => question.ItemId));
            Assert.Equal("Should this be retried?", snapshot.Live[0].Text);
        }
        else
        {
            Assert.Empty(snapshot.Live);
            Assert.Equal(["steward-1", clarification.Id], snapshot.Hidden.Select(item => item.Question.ItemId));
            Assert.All(snapshot.Hidden, item => Assert.Equal(expectedReason, item.Reason));
        }
        Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await store.GetAttentionQueueAsync()).Status);
        Assert.NotNull(harness.Kernel.Goals.Single().CurrentHold);
    }

    [Fact]
    public async Task DuplicateRequests_KeepEarliestWithStableTiesAndExactTextPerGoalWithoutADatabase()
    {
        var harness = new OwnerConsoleHarness();
        var first = harness.AddGoal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "First", AgentRole.Developer);
        var second = harness.AddGoal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Second", AgentRole.Developer);
        var requests = new[]
        {
            Request(first.Id.Value, "Same question") with { RequestedAt = RequestedAt.AddMinutes(2) },
            Request(first.Id.Value, "Same question") with { RequestedAt = RequestedAt.AddMinutes(1) },
            Request(first.Id.Value.ToUpperInvariant(), "Same question") with { RequestedAt = RequestedAt.AddMinutes(1) },
            Request(first.Id.Value, "same question"),
            Request(second.Id.Value, "Same question")
        };
        harness.Kernel.ReplaceWithSnapshot(harness.Kernel.ExportSnapshot() with { HumanInputRequests = requests });
        using var directory = new QuestionDirectory();
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "collaboration-items.db")));

        var snapshot = await new OwnerQuestionReadModel(harness.State, directory.Path)
            .ReadAsync(CancellationToken.None);

        Assert.Equal([requests[1].Id, requests[3].Id, requests[4].Id], snapshot.Live.Select(question => question.ItemId));
        Assert.Equal([requests[2].Id, requests[0].Id], snapshot.Hidden.Select(item => item.Question.ItemId));
        Assert.All(snapshot.Hidden, item => Assert.Equal("duplicate", item.Reason));
        Assert.All(harness.Kernel.HumanInputRequests, request => Assert.False(request.IsCompleted));
    }

    [Fact]
    public async Task MissingDatabase_PreservesHiddenReasonsAndOmitsZeroHiddenHeader()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111111111111111111111111111", "Active", AgentRole.Developer);
        var terminal = harness.AddGoal("22222222222222222222222222222222", "Cancelled", AgentRole.Developer);
        var requests = new[]
        {
            Request(goal.Id.Value, "Future acceptance evidence", HumanWaitKind.ProspectiveAcceptanceEvidence),
            Request(terminal.Id.Value, "Terminal future evidence", HumanWaitKind.ProspectiveAcceptanceEvidence),
            Request("55555555555555555555555555555555", "Missing goal request")
        };
        var before = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(before with
        {
            Goals = before.Goals.Select(item => item.Id == terminal.Id.Value
                ? item with { Status = GoalStatus.Cancelled } : item).ToArray(),
            HumanInputRequests = requests
        });
        using var directory = new QuestionDirectory();
        var source = new OwnerQuestionReadModel(harness.State, directory.Path);

        var snapshot = await source.ReadAsync(CancellationToken.None);

        Assert.Empty(snapshot.Live);
        Assert.Equal(new Dictionary<string, string>
        {
            [requests[0].Id] = "non-blocking-kind",
            [requests[1].Id] = "goal-cancelled",
            [requests[2].Id] = "goal-missing"
        }, snapshot.Hidden.ToDictionary(item => item.Question.ItemId, item => item.Reason));
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "collaboration-items.db")));

        var empty = new OwnerConsoleHarness();
        var emptySession = new OwnerConsoleSession(empty.State, new OwnerQuestionReadModel(empty.State, directory.Path),
            empty.Answers, empty.Liveness, empty.Digest, empty.Tail, empty.Output, empty.Clock);
        await emptySession.StartAsync(null, CancellationToken.None);
        Assert.Contains("owner questions: 0 | last event:", empty.Output.Text);
        Assert.DoesNotContain("hidden:", empty.Output.Text);
        Assert.DoesNotContain('\a', empty.Output.Text);
    }

    private static HumanInputRequestSnapshot Request(string goalId, string question,
        HumanWaitKind kind = HumanWaitKind.SpecClarification) =>
        new(Guid.NewGuid().ToString("N"), goalId, null, question, RequestedAt, kind);

    private sealed class QuestionDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "mcg-owner-live-questions-" + Guid.NewGuid().ToString("N"));

        internal QuestionDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
