using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: pure phrase mapping, instance-owned snapshots and a fixed clock; no GUI driver or I/O.
public sealed class OwnerConsoleHoldPhraseTests
{
    [Theory]
    [InlineData(ConductorHoldOwner.AcceptanceQueue, "waiting for gate")]
    [InlineData(ConductorHoldOwner.BackgroundAttempt, "focused tests running")]
    [InlineData(ConductorHoldOwner.WorkerCapacity, "waiting for a free worker")]
    [InlineData(ConductorHoldOwner.DurableOutbox, "waiting for spec refinement")]
    public void OwnersUsePlainPhrases(ConductorHoldOwner owner, string expected)
    {
        Assert.Equal(expected, OwnerConsoleHoldPhrase.ForOwner(owner));
        Assert.Equal(expected, OwnerConsoleHoldPhrase.For("untyped/" + owner));
    }

    [Theory]
    [InlineData("criterion-evidence-hold", "waiting for criterion evidence")]
    [InlineData("acceptance-apparatus-hold", "gate apparatus problem: operator retry needed")]
    [InlineData("environmental-apparatus-hold", "gate apparatus problem: operator retry needed")]
    [InlineData("evidence-mutation-lease-hold", "waiting for another evidence run")]
    [InlineData("replacement-evidence-mutation-lease", "waiting for another evidence run")]
    [InlineData("acceptance-width-admission", "waiting for a gate slot")]
    [InlineData("gate-start-infrastructure-deferred", "gate start deferred")]
    [InlineData("gate-start-build-slots-busy", "waiting for a build slot")]
    [InlineData("gate-start-build-lock-blocked", "waiting for the build lock")]
    [InlineData("attempt-running-in-background", "focused tests running")]
    public void TypedKindsUseTheirSpecificPhrase(string kind, string expected)
    {
        Assert.Equal(expected, OwnerConsoleHoldPhrase.For(kind));
        Assert.Equal(expected, OwnerConsoleHoldPhrase.For("verified-admission/" + kind, 3));
    }

    [Fact]
    public void EveryOwnerAndRecordedKindHasNoPascalCaseWord()
    {
        var owners = Enum.GetValues<ConductorHoldOwner>().Where(owner => owner != ConductorHoldOwner.None).ToArray();
        Assert.NotEmpty(owners);
        foreach (var owner in owners)
        {
            AssertPlain(OwnerConsoleHoldPhrase.ForOwner(owner));
            AssertPlain(OwnerConsoleHoldPhrase.For("untyped/" + owner));
        }

        var field = typeof(ConductorLandingBlockerRecorder).GetField("NextActions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var actions = Assert.IsAssignableFrom<IReadOnlyDictionary<string, (string Actor, string Verb)>>(field.GetValue(null));
        Assert.NotEmpty(actions);
        foreach (var kind in actions.Keys) AssertPlain(OwnerConsoleHoldPhrase.For("verified-admission/" + kind));
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    [InlineData(23, "23rd")]
    public void QueuePositionIsUsedOnlyForAcceptanceQueue(int position, string ordinal)
    {
        Assert.Equal($"waiting for gate ({ordinal} in queue)",
            OwnerConsoleHoldPhrase.For("untyped/AcceptanceQueue", position));
        Assert.Equal("focused tests running", OwnerConsoleHoldPhrase.For("untyped/BackgroundAttempt", position));
    }

    [Theory]
    [InlineData("stage/some-new-hold", "some new hold")]
    [InlineData("some-new-hold", "some new hold")]
    [InlineData("stage/outer/some-new-hold", "some new hold")]
    [InlineData("untyped/UnknownOwner", "unknown owner")]
    public void UnknownKindsUsePlainDiscriminator(string kind, string expected) =>
        Assert.Equal(expected, OwnerConsoleHoldPhrase.For(kind));

    [Fact]
    public async Task BoardCombinesQueuePositionAndAge()
    {
        var now = new DateTimeOffset(2026, 10, 28, 16, 0, 0, TimeSpan.Zero);
        var clock = new OwnerConsoleTestClock { Now = now };
        var harness = new OwnerConsoleHarness(clock);
        for (var index = 1; index <= 3; index++)
            harness.AddGoal($"{index}1111111-queued", "Verify the feature", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = GoalStatus.Verified,
                Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray(),
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "entered Verified", now.AddHours(-2))],
                LandingBlocker = new("untyped/AcceptanceQueue", "queue", "queue", now.AddMinutes(-108), "unmapped", "unmapped")
            }).ToArray()
        });
        var model = await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new Epics(), clock, gateProgress: _ => OwnerConsoleGateProgress.Idle()).BuildBoardAsync();

        Assert.Equal(3, model.Board.Length);
        var row = Assert.Single(model.Board, row => row.GoalId == "31111111-queued");
        Assert.Equal(OwnerConsoleAttention.WaitingForGate, row.SortKey.Rank);
        Assert.Equal("waiting for gate (3rd in queue), 1h48m", row.Stage);
        Assert.Equal(row.Stage, row.StageWithoutQuestion);
    }

    private static void AssertPlain(string phrase)
    {
        Assert.False(string.IsNullOrWhiteSpace(phrase));
        Assert.DoesNotMatch(@"\b[A-Z][a-z]+[A-Z]\w*", phrase);
        foreach (var name in Enum.GetNames<ConductorHoldOwner>()) Assert.DoesNotContain(name, phrase);
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("");
    }
}
