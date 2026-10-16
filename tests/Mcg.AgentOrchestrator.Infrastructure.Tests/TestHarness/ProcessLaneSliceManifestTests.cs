using System.Text.Json.Nodes;

// Parallel-safe: reads repository files; negative controls mutate only in-memory JSON.
public sealed class ProcessLaneSliceManifestTests
{
    private static readonly string[] RetiredLanes = ["Worker shell", "Worker sandbox planner"];
    private static readonly string[] MovedTypes = ["WorkerShellTests", "WorkerSandboxCapabilityPlannerTests"];

    [Fact]
    public void Manifest_MovedLanesRetired_HasNoViolations()
    {
        Assert.Empty(Violations(ReadManifest()));
    }

    [Fact]
    public void Manifest_RetiredLaneRestored_ReportsViolation()
    {
        var manifest = ReadManifest().DeepClone();
        manifest["engine"]!["infrastructureTestLanes"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "Worker shell",
            ["filter"] = "FullyQualifiedName~WorkerShellTests",
            ["estimatedSerialSeconds"] = 8.6
        });

        Assert.Contains(Violations(manifest), violation =>
            violation.Contains("Worker shell", StringComparison.Ordinal));
    }

    private static JsonNode ReadManifest() => JsonNode.Parse(File.ReadAllText(Path.Combine(
        InfrastructureTestSupport.FindRepositoryRoot(), "config", "acceptance-manifest.json")))!;

    private static IReadOnlyList<string> Violations(JsonNode manifest)
    {
        var violations = new List<string>();
        var engine = manifest["engine"]!.AsObject();
        var infrastructureLanes = engine["infrastructureTestLanes"]!.AsArray();
        if (infrastructureLanes.Count != 25)
            violations.Add($"Expected 25 infrastructure lanes, found {infrastructureLanes.Count}.");

        foreach (var lane in infrastructureLanes)
        {
            var name = lane!["name"]!.GetValue<string>();
            if (RetiredLanes.Contains(name, StringComparer.Ordinal))
                violations.Add($"Retired lane remains: {name}.");
        }

        var laneNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in engine.Where(property =>
                     property.Key.EndsWith("TestLanes", StringComparison.Ordinal)))
        {
            foreach (var lane in property.Value!.AsArray())
            {
                var name = lane!["name"]!.GetValue<string>();
                laneNames.Add(name);
                var filter = lane["filter"]!.GetValue<string>();
                foreach (var typeName in MovedTypes)
                {
                    if (filter.Contains(typeName, StringComparison.Ordinal))
                        violations.Add($"Lane {name} still filters {typeName}.");
                }
            }
        }

        foreach (var partition in engine["localTestPartitions"]!.AsArray())
        {
            var partitionName = partition!["name"]!.GetValue<string>();
            foreach (var lane in partition["laneNames"]!.AsArray())
            {
                var name = lane!.GetValue<string>();
                if (!laneNames.Contains(name))
                    violations.Add($"Partition {partitionName} names unknown lane {name}.");
                if (partitionName == "WorkerDispatch" && RetiredLanes.Contains(name, StringComparer.Ordinal))
                    violations.Add($"WorkerDispatch still names retired lane {name}.");
            }
        }

        return violations;
    }
}
