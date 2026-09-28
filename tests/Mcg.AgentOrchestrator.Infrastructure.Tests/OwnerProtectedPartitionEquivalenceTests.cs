using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerProtectedPartitionEquivalenceTests : IDisposable
{
    private const string Main = """
        {"version":1,"engine":{"maxConcurrentShards":2,"timeouts":{"defaultMinutes":40},
          "infrastructureTestLanes":[
            {"name":"Spawn","filter":"FullyQualifiedName~Alpha|FullyQualifiedName~Beta|FullyQualifiedName~Gamma"},
            {"name":"Remainder","filter":"FullyQualifiedName~Other"}],
          "localTestPartitions":[{"name":"P","laneNames":["Spawn","Remainder"]}]}}
        """;
    private const string Candidate = """
        {"version":1,"engine":{"maxConcurrentShards":2,"timeouts":{"defaultMinutes":40},
          "infrastructureTestLanes":[
            {"name":"Spawn","filter":"FullyQualifiedName~Alpha"},
            {"name":"Local","filter":"FullyQualifiedName~Beta|FullyQualifiedName~Gamma",
             "ownedCollections":["LocalCol"],"exclusiveResourceKeys":["xunit:LocalCol"]},
            {"name":"Remainder","filter":"FullyQualifiedName~Other"}],
          "localTestPartitions":[{"name":"P","laneNames":["Spawn","Local","Remainder"]}]}}
        """;
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Fact]
    public void MovingTwoClassesToProcessLocalLanePassesWithoutApproval()
    {
        var result = Evaluate(Candidate);
        Xunit.Assert.Null(result.Failure);
        Xunit.Assert.Equal("partition-equivalent", Xunit.Assert.IsType<AcceptanceCheckResult>(result.Pass).ResultSummary);
    }

    [Xunit.Theory]
    [Xunit.InlineData("drop")]
    [Xunit.InlineData("duplicate")]
    [Xunit.InlineData("concurrency")]
    [Xunit.InlineData("timeout")]
    [Xunit.InlineData("unknown-lane-field")]
    [Xunit.InlineData("invalid-json")]
    public void NonEquivalentManifestStillNeedsApproval(string mutation)
    {
        var candidate = JsonNode.Parse(Candidate)!.AsObject();
        var engine = candidate["engine"]!.AsObject();
        var lanes = engine["infrastructureTestLanes"]!.AsArray();
        switch (mutation)
        {
            case "drop":
                lanes[1]!["filter"] = "FullyQualifiedName~Beta";
                lanes[1]!["ownedCollections"] = new JsonArray();
                break;
            case "duplicate": lanes[0]!["filter"] = "FullyQualifiedName~Alpha|FullyQualifiedName~Beta"; break;
            case "concurrency": engine["maxConcurrentShards"] = 3; break;
            case "timeout": engine["timeouts"]!["defaultMinutes"] = 41; break;
            case "unknown-lane-field": lanes[1]!["requiresBuildSystemChange"] = true; break;
        }
        var result = Evaluate(mutation == "invalid-json" ? "{invalid" : candidate.ToJsonString());
        Xunit.Assert.Null(result.Pass);
        Xunit.Assert.Equal("operator review required", Xunit.Assert.IsType<AcceptanceCheckResult>(result.Failure).ResultSummary);
    }

    [Xunit.Fact]
    public void ChangedConductorPolicyPreventsEquivalentManifestPass()
    {
        WriteCandidate(Candidate);
        File.WriteAllText(Path.Combine(_root, "config", "conductor-policy.json"), """{"maxRetries":4}""");
        var result = GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, null,
            ["config/acceptance-manifest.json", "config/conductor-policy.json"], null,
            (_, args) => args[0] == "diff" ? "config/acceptance-manifest.json\nconfig/conductor-policy.json\n" :
                args[0] == "show" && args[1].EndsWith("conductor-policy.json", StringComparison.Ordinal)
                    ? """{"maxRetries":3}""" : Main,
            null, Inventory, new string('a', 40));
        Xunit.Assert.Null(result.Pass);
        Xunit.Assert.Equal("operator review required", Xunit.Assert.IsType<AcceptanceCheckResult>(result.Failure).ResultSummary);
    }

    [Xunit.Fact]
    public void NonProcessLocalCollectionSpanningLanesNeedsSharedKey()
    {
        var inventory = new AcceptanceTestInventory(
            [new("Tests.Alpha", "SharedCol"), new("Tests.Beta", "SharedCol"),
             new("Tests.Gamma", "LocalCol"), new("Tests.Other", null)],
            new HashSet<string>(["SharedCol", "LocalCol"], StringComparer.Ordinal),
            new HashSet<string>(["LocalCol"], StringComparer.Ordinal));
        var result = Evaluate(Candidate, () => inventory);
        Xunit.Assert.Null(result.Pass);
        Xunit.Assert.Equal("operator review required", Xunit.Assert.IsType<AcceptanceCheckResult>(result.Failure).ResultSummary);
        var localAtMain = inventory with
        {
            ProcessLocalCollectionsAtMain = new HashSet<string>(["SharedCol", "LocalCol"], StringComparer.Ordinal)
        };
        Xunit.Assert.Equal("partition-equivalent", Evaluate(Candidate, () => localAtMain).Pass?.ResultSummary);
    }

    [Xunit.Fact]
    public void UnavailableInventoryFailsClosed()
    {
        var result = Evaluate(Candidate, () => throw new InvalidDataException("metadata unavailable"));
        Xunit.Assert.Null(result.Pass);
        Xunit.Assert.Equal("operator review required", Xunit.Assert.IsType<AcceptanceCheckResult>(result.Failure).ResultSummary);
    }

    private GoalAcceptanceVerifier.OwnerProtectedDecision Evaluate(string candidate,
        Func<AcceptanceTestInventory>? inventory = null)
    {
        WriteCandidate(candidate);
        return GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, null,
            ["config/acceptance-manifest.json"], null,
            (_, args) => args[0] == "diff" ? "config/acceptance-manifest.json\n" : Main, null,
            inventory ?? Inventory, new string('a', 40));
    }

    private void WriteCandidate(string candidate)
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), candidate);
    }

    private static AcceptanceTestInventory Inventory() => new(
        [new("Tests.Alpha", "SpawnCol"), new("Tests.Beta", "LocalCol"),
         new("Tests.Gamma", "LocalCol"), new("Tests.Other", null)],
        new HashSet<string>(["SpawnCol", "LocalCol"], StringComparer.Ordinal),
        new HashSet<string>(["LocalCol"], StringComparer.Ordinal));

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
