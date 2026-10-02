using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its temporary workspace and real SQLite store.
public sealed class OperatorInboxParkedWaitEscalationTests : IDisposable
{
    private readonly string _root = SharedTestSupport.CreateTempDirectory();
    private static readonly DateTimeOffset ResolvedAt = DateTimeOffset.Parse("2026-10-02T20:00:00Z");
    private OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(_root);
    private CollaborationItemStore Store => CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);

    [Theory(DisplayName = "Leaving either parked wait resolves its record and attention item")]
    [InlineData(GoalLifecycleState.AwaitingClarification)]
    [InlineData(GoalLifecycleState.AwaitingHumanInput)]
    public async Task LeavingParkedStateResolvesRecordAndItem(GoalLifecycleState parked)
    {
        var goal = NewGoal();
        var raised = await RecordAsync(goal, $"conductor:{parked}");
        Assert.Equal(CollaborationItemStatus.Raised, raised.Status);
        Assert.Equal(JsonValueKind.Null, ReadRecords().Single().GetProperty("resolvedAtUtc").ValueKind);

        Assert.Equal(1, await ResolveAsync(goal, GoalLifecycleState.WorkspaceReady));

        var resolved = Assert.Single(await Store.ListAsync(goal.Id.Value));
        Assert.Equal(raised.Id, resolved.Id);
        Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        var record = Assert.Single(ReadRecords());
        Assert.Equal(ResolvedAt, record.GetProperty("resolvedAtUtc").GetDateTimeOffset());
        Assert.Equal("conductor-state-left", record.GetProperty("resolvedBy").GetString());
        Assert.Contains($"conductor:{parked}", record.GetProperty("resolutionReason").GetString());
        Assert.Equal(record.GetProperty("resolutionReason").GetString(), resolved.Resolution);
    }

    [Theory(DisplayName = "Remaining in the same parked wait keeps record and item open")]
    [InlineData(GoalLifecycleState.AwaitingClarification)]
    [InlineData(GoalLifecycleState.AwaitingHumanInput)]
    public async Task SameParkedStateKeepsItemRaised(GoalLifecycleState parked)
    {
        var goal = NewGoal();
        await RecordAsync(goal, $"conductor:{parked}");
        var before = File.ReadAllText(RecordPath);

        Assert.Equal(0, await ResolveAsync(goal, parked));

        Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await Store.ListAsync(goal.Id.Value)).Status);
        Assert.Equal(before, File.ReadAllText(RecordPath));
    }

    [Theory(DisplayName = "Moving between waits resolves the wait that was left")]
    [InlineData(GoalLifecycleState.AwaitingClarification, GoalLifecycleState.AwaitingHumanInput)]
    [InlineData(GoalLifecycleState.AwaitingHumanInput, GoalLifecycleState.AwaitingClarification)]
    public async Task DifferentParkedStateResolvesOldWait(GoalLifecycleState parked, GoalLifecycleState current)
    {
        var goal = NewGoal();
        await RecordAsync(goal, $"conductor:{parked}");

        Assert.Equal(1, await ResolveAsync(goal, current));
        Assert.Equal(CollaborationItemStatus.Resolved, Assert.Single(await Store.ListAsync(goal.Id.Value)).Status);
    }

    [Theory(DisplayName = "Other conductor states and landing branches stay open for every lifecycle state")]
    [InlineData("conductor:Failed")]
    [InlineData("conductor:Blocked")]
    [InlineData("conductor:Verified")]
    [InlineData("integration")]
    public async Task OtherBranchesStayRaised(string branch)
    {
        var goal = NewGoal();
        await RecordAsync(goal, branch);
        var before = File.ReadAllText(RecordPath);

        foreach (var current in Enum.GetValues<GoalLifecycleState>())
        {
            Assert.Equal(0, await ResolveAsync(goal, current));
            Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await Store.ListAsync(goal.Id.Value)).Status);
            Assert.Equal(before, File.ReadAllText(RecordPath));
        }
    }

    [Fact(DisplayName = "Parking again with the same reason raises a fresh attention item")]
    public async Task ReRaiseAfterResolutionCreatesNewItem()
    {
        var goal = NewGoal();
        var first = await RecordAsync(goal, "conductor:AwaitingClarification");
        Assert.Equal(1, await ResolveAsync(goal, GoalLifecycleState.WorkspaceReady));
        Assert.Equal(CollaborationItemStatus.Resolved, Assert.Single(await Store.ListAsync(goal.Id.Value)).Status);

        OperatorInbox.RecordLandingEscalation(Workspace, goal, "waiting for answer", "conductor:AwaitingClarification",
            collaborationStore: Store, collaborationRaiseTimeout: Timeout.InfiniteTimeSpan);

        var items = await Store.ListAsync(goal.Id.Value);
        Assert.Equal(2, items.Count);
        Assert.Equal(CollaborationItemStatus.Resolved, Assert.Single(items, item => item.Id == first.Id).Status);
        var next = Assert.Single(items, item => item.Status == CollaborationItemStatus.Raised);
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(first.CorrelationKey, next.CorrelationKey);
        Assert.Equal(2, ReadRecords().Length);
        Assert.Single(ReadRecords(), record => record.GetProperty("resolvedAtUtc").ValueKind == JsonValueKind.Null);
    }

    [Fact(DisplayName = "Resolving one goal leaves another goal's parked wait open")]
    public async Task OtherGoalIsUntouched()
    {
        var goal = NewGoal();
        var other = NewGoal();
        await RecordAsync(goal, "conductor:AwaitingClarification");
        await RecordAsync(other, "conductor:AwaitingHumanInput");

        Assert.Equal(1, await ResolveAsync(goal, GoalLifecycleState.WorkspaceReady));

        Assert.Equal(CollaborationItemStatus.Resolved, Assert.Single(await Store.ListAsync(goal.Id.Value)).Status);
        Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await Store.ListAsync(other.Id.Value)).Status);
        Assert.Equal(JsonValueKind.Null, Assert.Single(ReadRecords(), record =>
            record.GetProperty("goalId").GetString() == other.Id.Value).GetProperty("resolvedAtUtc").ValueKind);
    }

    [Fact(DisplayName = "A manually resolved item still allows its escalation record to converge")]
    public async Task AlreadyResolvedItemStillStampsRecord()
    {
        var goal = NewGoal();
        var raised = await RecordAsync(goal, "conductor:AwaitingClarification");
        Assert.True(await Store.TryResolveAsync(raised.CorrelationKey!, "operator dismissed"));

        Assert.Equal(1, await ResolveAsync(goal, GoalLifecycleState.WorkspaceReady));
        Assert.Equal("conductor-state-left", Assert.Single(ReadRecords()).GetProperty("resolvedBy").GetString());
        Assert.Equal("operator dismissed", Assert.Single(await Store.ListAsync(goal.Id.Value)).Resolution);
        var before = File.ReadAllText(RecordPath);
        Assert.Equal(0, await ResolveAsync(goal, GoalLifecycleState.AwaitingHumanInput));
        Assert.Equal(before, File.ReadAllText(RecordPath));
    }

    [Fact(DisplayName = "An acceptance retry resolution keeps its existing audit stamp")]
    public async Task AcceptanceRetryStampIsPreserved()
    {
        var goal = NewGoal();
        await RecordAsync(goal, "conductor:AwaitingClarification");
        Assert.Equal(OperatorInbox.LandingEscalationResolution.Resolved, OperatorInbox.ResolveLandingEscalationOccurrence(
            Workspace, goal, "retry approved", ResolvedAt, resolvedAtUtc: ResolvedAt));
        var before = File.ReadAllText(RecordPath);

        Assert.Equal(0, await ResolveAsync(goal, GoalLifecycleState.WorkspaceReady));
        Assert.Equal(before, File.ReadAllText(RecordPath));
        Assert.Equal("acceptance-retry", Assert.Single(ReadRecords()).GetProperty("resolvedBy").GetString());
        Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await Store.ListAsync(goal.Id.Value)).Status);
    }

    private static Goal NewGoal() => new(GoalId.New(), "Parked wait test",
        [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);

    private async Task<CollaborationItem> RecordAsync(Goal goal, string branch)
    {
        OperatorInbox.RecordLandingEscalation(Workspace, goal, "waiting for answer", branch,
            collaborationStore: Store, collaborationRaiseTimeout: Timeout.InfiniteTimeSpan);
        return Assert.Single(await Store.ListAsync(goal.Id.Value));
    }

    private Task<int> ResolveAsync(Goal goal, GoalLifecycleState state) =>
        OperatorInbox.ResolveParkedWaitLandingEscalationsAsync(Workspace, goal, state, Store, ResolvedAt);

    private string RecordPath => Path.Combine(Workspace.OrchestratorDirectory, "landing-escalations.json");

    private JsonElement[] ReadRecords()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RecordPath));
        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
