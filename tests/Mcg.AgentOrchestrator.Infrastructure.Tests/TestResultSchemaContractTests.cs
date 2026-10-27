using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the schema is read-only and the negative control mutates only an in-memory clone.
public sealed class TestResultSchemaContractTests
{
    [Fact]
    public void Schema_MatchesReaderContract_AndDetectsAdditionalStatus()
    {
        var root = VerifiedRepositoryRoot.Find();
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "docs", "contracts",
            "test-result-record.v1.schema.json")))!.AsObject();
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.False(ContainsReference(schema));
        Assert.Equal(new[] { "reportFormat", "schemaVersion", "results" }, Strings(schema["required"]!));
        Assert.Equal("CTRF", schema["properties"]!["reportFormat"]!["const"]!.GetValue<string>());
        Assert.Equal(NormalizedTestResultSource.SchemaVersion,
            schema["properties"]!["schemaVersion"]!["const"]!.GetValue<string>());
        var results = schema["properties"]!["results"]!;
        Assert.Equal(new[] { "tool", "tests" }, Strings(results["required"]!));
        Assert.Equal(new[] { "name" }, Strings(results["properties"]!["tool"]!["required"]!));
        var test = TestSchema(schema);
        Assert.Equal(new[] { "testId", "name", "status" }, Strings(test["required"]!));
        var properties = test["properties"]!.AsObject();
        Assert.Equal(NormalizedTestResultSource.StatusValues.ToArray(), Strings(properties["status"]!["enum"]!));
        var attempt = properties["retryAttempts"]!["items"]!;
        Assert.Equal(new[] { "status" }, Strings(attempt["required"]!));
        Assert.Equal(NormalizedTestResultSource.StatusValues.ToArray(),
            Strings(attempt["properties"]!["status"]!["enum"]!));
        Assert.Equal(Enum.GetNames<TestResultStatus>().Select(name => name.ToLowerInvariant()),
            NormalizedTestResultSource.StatusValues);
        Assert.Equal(NormalizedTestResultSource.TestPropertyNames.Order(StringComparer.Ordinal),
            properties.Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Empty(StatusDifference(schema));

        var changed = schema.DeepClone();
        TestSchema(changed)["properties"]!["status"]!["enum"]!.AsArray().Add("pending");
        Assert.Equal(new[] { "pending" }, StatusDifference(changed));
    }

    private static JsonNode TestSchema(JsonNode schema) =>
        schema["properties"]!["results"]!["properties"]!["tests"]!["items"]!;

    private static string[] StatusDifference(JsonNode schema)
    {
        var difference = Strings(TestSchema(schema)["properties"]!["status"]!["enum"]!)
            .ToHashSet(StringComparer.Ordinal);
        difference.SymmetricExceptWith(NormalizedTestResultSource.StatusValues);
        return difference.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] Strings(JsonNode node) =>
        node.AsArray().Select(value => value!.GetValue<string>()).ToArray();

    private static bool ContainsReference(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(property => property.Key == "$ref" || ContainsReference(property.Value)),
        JsonArray array => array.Any(ContainsReference),
        _ => false
    };
}
