using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class InFlightTrainLandingHoldTests
{
    private static readonly GoalId Goal = new("1234567890abcdef1234567890abcdef");

    [Fact]
    public void Describe_NonMemberTrain_NamesTrainAndGoalPrefix()
    {
        const string key = "train:a+b+c";
        Assert.Equal(Reason(key), InFlightTrainLandingHold.Describe(Goal, Snapshot(key)));
    }

    [Fact]
    public void Describe_TrainMember_ReturnsNull()
    {
        Assert.Null(InFlightTrainLandingHold.Describe(Goal, Snapshot("train:a+b+c", Goal.Value)));
    }

    [Fact]
    public void Describe_CohortPair_ReturnsNull()
    {
        Assert.Null(InFlightTrainLandingHold.Describe(Goal, Snapshot($"{Guid.NewGuid():N}:{Guid.NewGuid():N}")));
    }

    [Fact]
    public void Describe_Follower_ReturnsNull()
    {
        Assert.Null(InFlightTrainLandingHold.Describe(Goal, Snapshot($"follower:{Guid.NewGuid():N}")));
    }

    [Fact]
    public void Describe_EmptySnapshot_ReturnsNull()
    {
        Assert.Null(InFlightTrainLandingHold.Describe(Goal, new ConductorAcceptanceCapacitySnapshot([])));
    }

    [Fact]
    public void Describe_RestoredTrainAttempt_NamesFullKey()
    {
        const string key = "train:a+b+c|attempt-1";
        Assert.Equal(Reason(key), InFlightTrainLandingHold.Describe(Goal, Snapshot(key)));
    }

    [Fact]
    public void Describe_MultipleRoots_SelectsFirstOrdinalTrainExcludingGoal()
    {
        var roots = new[]
        {
            Snapshot("train:z").ActiveRoots[0],
            Snapshot("train:0", Goal.Value).ActiveRoots[0],
            Snapshot("Train:0").ActiveRoots[0],
            Snapshot("train:a").ActiveRoots[0]
        };
        Assert.Equal(Reason("train:a"),
            InFlightTrainLandingHold.Describe(Goal, new ConductorAcceptanceCapacitySnapshot(roots)));
    }

    private static ConductorAcceptanceCapacitySnapshot Snapshot(string key, string? member = null) =>
        new([new ConductorAcceptanceCapacityRoot(key,
            new HashSet<string>(StringComparer.Ordinal) { member ?? Guid.NewGuid().ToString("N") })]);

    private static string Reason(string key) =>
        $"merge train {key} is in flight and goal 12345678 is not a member; landing waits for the train to settle";
}
