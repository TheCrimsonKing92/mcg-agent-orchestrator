using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: every report is written beneath an owned fixture directory.
public sealed class ImpactReplayUnmappedTestClassReportTests
{
    [Fact]
    public void ReportsRankThirtyClassesAndCountAllDistinctOccurrences()
    {
        using var fixture = new ImpactReplayFixture();
        var occurrences = new List<ImpactReplayUnmappedTestClasses.Occurrence>
        {
            new("a", "ZetaTests", "not-scanned", []),
            new("b", "ZetaTests", "not-scanned", []),
            new("c", "ZetaTests", "not-scanned", []),
            new("a", "ZetaTests", "not-scanned", []),
            new("a", "MidTests", "no-lane", ["Z.MidTests"]),
            new("b", "MidTests", "no-lane", ["A.MidTests"]),
            new("a", "AlphaTests", "ambiguous", ["A.AlphaTests", "B.AlphaTests"]),
            new("b", "AlphaTests", "not-scanned", [])
        };
        for (var i = 0; i < 31; i++)
            occurrences.Add(new("filler", $"Filler{i:00}Tests", "not-scanned", []));
        var diagnostics = ImpactReplayUnmappedTestClasses.Aggregate(occurrences);
        var report = new ImpactReplayReport("default", [], [], [], [], [], UnmappedTestClasses: diagnostics);

        ImpactReplayReportWriter.Write(fixture.OutputDirectory, report);

        var markdown = File.ReadAllText(Path.Combine(fixture.OutputDirectory, "impact-replay.md"));
        var jsonText = File.ReadAllText(Path.Combine(fixture.OutputDirectory, "impact-replay.json"));
        var json = JsonNode.Parse(jsonText)!["unmappedTestClasses"]!;
        Assert.Equal(30, json["topClasses"]!.AsArray().Count);
        Assert.Equal(38, json["occurrences"]!.AsArray().Count);
        Assert.Equal(35, json["countsByReason"]!["not-scanned"]!.GetValue<int>());
        Assert.Equal(2, json["countsByReason"]!["no-lane"]!.GetValue<int>());
        Assert.Equal(1, json["countsByReason"]!["ambiguous"]!.GetValue<int>());
        Assert.Contains("## Unmapped test classes", markdown);
        Assert.Contains("- not-scanned: 35", markdown);
        Assert.Contains("- no-lane: 2", markdown);
        Assert.Contains("- ambiguous: 1", markdown);

        var section = markdown[markdown.IndexOf("## Unmapped test classes", StringComparison.Ordinal)..];
        var rows = section.Split('\n').Where(line => line.StartsWith("| ", StringComparison.Ordinal)).Skip(2).ToArray();
        Assert.Equal(30, rows.Length);
        Assert.Equal("| ZetaTests | not-scanned | 3 |  |", rows[0].TrimEnd('\r'));
        Assert.Equal("| MidTests | no-lane | 2 | A.MidTests, Z.MidTests |", rows[1].TrimEnd('\r'));
        Assert.Equal("| AlphaTests | ambiguous | 1 | A.AlphaTests, B.AlphaTests |", rows[2].TrimEnd('\r'));
        Assert.Equal("| AlphaTests | not-scanned | 1 |  |", rows[3].TrimEnd('\r'));
        Assert.Equal("Filler25Tests", json["topClasses"]![29]!["class"]!.GetValue<string>());
        for (var i = 0; i < rows.Length; i++)
        {
            var item = json["topClasses"]![i]!;
            var names = item["fullNames"]!.AsArray().Select(name => name!.GetValue<string>());
            Assert.Equal($"| {item["class"]!.GetValue<string>()} | {item["reason"]!.GetValue<string>()} | {item["attempts"]!.GetValue<int>()} | {string.Join(", ", names)} |",
                rows[i].TrimEnd('\r'));
        }
        var roundTrip = JsonSerializer.Deserialize<ImpactReplayReport>(jsonText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(38, roundTrip.UnmappedTestClasses!.Occurrences.Count);
        Assert.Equal(new[] { "A.MidTests", "Z.MidTests" }, roundTrip.UnmappedTestClasses.TopClasses[1].FullNames);
        Assert.Equal(new[] { "Z.MidTests" }, Assert.Single(roundTrip.UnmappedTestClasses.Occurrences,
            item => item.AttemptId == "a" && item.Class == "MidTests").FullNames);
    }

    [Fact]
    public void FixtureReportRoundTripsOccurrencesToJsonAndMarkdown()
    {
        using var fixture = new ImpactReplayFixture(changedTestFiles: new Dictionary<string, string>
        {
            ["GammaTests.cs"] = "namespace SampleTests; public class GammaTests { [Xunit.Fact] public void Works() { } }"
        });
        fixture.AddAttempt("round-trip");

        var (report, markdown) = fixture.Run();

        var occurrence = Assert.Single(report.UnmappedTestClasses!.Occurrences);
        Assert.Equal("round-trip", occurrence.AttemptId);
        Assert.Equal("GammaTests", occurrence.Class);
        Assert.Equal("no-lane", occurrence.Reason);
        Assert.Equal(new[] { "SampleTests.GammaTests" }, occurrence.FullNames);
        Assert.Contains("| GammaTests | no-lane | 1 | SampleTests.GammaTests |", markdown);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.OutputDirectory, "impact-replay.json")))!;
        Assert.Equal(1, json["unmappedTestClasses"]!["countsByReason"]!["no-lane"]!.GetValue<int>());
    }

    [Fact]
    public void LegacyReportWithoutDiagnosticsWritesAnExplicitEmptySection()
    {
        using var fixture = new ImpactReplayFixture();
        const string legacyJson = """
            { "floorSource": "default", "floorLanes": [], "attempts": [], "skippedInputs": [],
              "unreplayableAttempts": [], "cleanupErrors": [], "attemptsGainingChangedTestClasses": 0 }
            """;
        var report = JsonSerializer.Deserialize<ImpactReplayReport>(legacyJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(report.UnmappedTestClasses);

        ImpactReplayReportWriter.Write(fixture.OutputDirectory, report);

        var markdown = File.ReadAllText(Path.Combine(fixture.OutputDirectory, "impact-replay.md"));
        Assert.Contains("## Unmapped test classes", markdown);
        Assert.Contains("No unmapped test classes were found.", markdown);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.OutputDirectory, "impact-replay.json")))!["unmappedTestClasses"]!;
        Assert.Equal(3, json["countsByReason"]!.AsObject().Count);
        Assert.All(json["countsByReason"]!.AsObject(), item => Assert.Equal(0, item.Value!.GetValue<int>()));
        Assert.Empty(json["topClasses"]!.AsArray());
        Assert.Empty(json["occurrences"]!.AsArray());
    }
}
