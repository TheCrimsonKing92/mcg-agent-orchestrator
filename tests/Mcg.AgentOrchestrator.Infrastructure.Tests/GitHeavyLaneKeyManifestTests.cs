using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: reads shipped settings without mutating shared state.
public sealed class GitHeavyLaneKeyManifestTests
{
    private const string SharedKey = "machine:GitHeavyLanes";
    private static readonly string[] GitHeavyLaneNames =
    [
        "Remainder",
        "Remainder balance A",
        "Remainder merge train and ratchet"
    ];

    [Fact]
    public void ShippedManifest_OnlyThreeGitHeavyLanesDeclareTheSharedKey()
    {
        var lanes = LoadLanes();
        Assert.Equal(27, lanes.Count);
        var keyedLanes = lanes.Where(lane => lane.ExclusiveResourceKeys.Contains(SharedKey)).ToArray();
        Assert.Equal(GitHeavyLaneNames, keyedLanes.Select(lane => lane.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.All(keyedLanes, lane =>
        {
            Assert.Equal(new[] { SharedKey }, lane.ExclusiveResourceKeys);
            Assert.Empty(lane.OwnedCollections);
            Assert.False(lane.RequiresBuildSystemChange);
        });
        Assert.All(lanes.Where(lane => !GitHeavyLaneNames.Contains(lane.Name)),
            lane => Assert.DoesNotContain(SharedKey, lane.ExclusiveResourceKeys));
    }

    [Fact]
    public void RemoteEligibility_SharedKeyMustBeDeclaredMachineLocal()
    {
        var lanes = LoadLanes();
        string[] shippedMachineLocalKeys =
        [
            "xunit:EnvMutation",
            "xunit:ProcessSpawningProcessLocal",
            "xunit:ProcessSpawning",
            "xunit:DotnetBuildSlots"
        ];
        string[] extendedMachineLocalKeys = [.. shippedMachineLocalKeys, SharedKey];
        foreach (var name in GitHeavyLaneNames)
        {
            var lane = Assert.Single(lanes, lane => lane.Name == name);
            Assert.Equal("exclusive-key:" + SharedKey, RemoteLaneResourceEligibility.ExclusionReason(
                lane.ExclusiveResourceKeys, lane.RequiresBuildSystemChange, shippedMachineLocalKeys));
            Assert.Null(RemoteLaneResourceEligibility.ExclusionReason(
                lane.ExclusiveResourceKeys, lane.RequiresBuildSystemChange, extendedMachineLocalKeys));
        }

        var keylessLane = Assert.Single(lanes, lane => lane.Name == "Remainder balance B");
        Assert.Null(RemoteLaneResourceEligibility.ExclusionReason(
            keylessLane.ExclusiveResourceKeys, keylessLane.RequiresBuildSystemChange, shippedMachineLocalKeys));
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
}
