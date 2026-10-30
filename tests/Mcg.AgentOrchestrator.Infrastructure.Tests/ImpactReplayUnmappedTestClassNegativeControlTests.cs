using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each case owns its Git repository, receipts and output files.
public sealed class ImpactReplayUnmappedTestClassNegativeControlTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void MappedFixturesKeepLegacyFiguresAndMarkdownPrefix(int sourceCount)
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: sourceCount);
        fixture.AddAttempt("control");

        var (report, markdown) = fixture.Run();

        var abandoned = sourceCount == 6;
        var attempt = Assert.Single(report.Attempts);
        Assert.Equal(abandoned ? "Abandoned" : "Resolved", attempt.SelectionOutcome);
        Assert.Equal(abandoned ? "ChangedSourceFileCount" : null, attempt.DegradationKind);
        Assert.Equal(new[] { "Alpha lane", "Beta lane", "Chaos gate" }, attempt.RanLanes);
        Assert.Equal(abandoned ? new[] { "Alpha lane", "Beta lane", "Chaos gate" } : new[] { "Alpha lane", "Chaos gate" }, attempt.WouldRunLanes);
        Assert.Equal(abandoned ? Array.Empty<string>() : new[] { "Beta lane" }, attempt.SkippedLanes);
        Assert.Equal(abandoned ? 0 : 1d / 3, attempt.ShareOfLanesSkipped);
        Assert.Equal(abandoned ? 0 : 23.5, attempt.EstimatedSerialSecondsSaved);
        Assert.Empty(attempt.FailingClasses);
        Assert.True(attempt.MissCheckComplete);
        Assert.Equal(0, report.AttemptsGainingChangedTestClasses);

        var legacyLines = new[]
        {
            "Floor source: default",
            "Default floor policy: Process spawning*, Isolated process spawning*, Chaos gate, Remainder merge train and ratchet.",
            "Floor lanes: Chaos gate",
            "",
            "Attempts replayed: 1",
            "Unreplayable attempts: 0",
            "Skipped inputs: 0",
            "Incomplete miss checks: 0",
            $"Median share of lanes skipped: {(abandoned ? "0.0 %" : "33.3 %")}",
            $"P90 share of lanes skipped: {(abandoned ? "0.0 %" : "33.3 %")}",
            $"Total estimated lane-seconds saved: {(abandoned ? "0" : "23.5")}",
            "",
            "## Selections abandoned, by kind",
            "",
            abandoned ? "- ChangedSourceFileCount: 1" : "None.",
            "",
            "## Misses",
            "",
            "None observed; consult incomplete miss checks before drawing conclusions.",
            "",
            "## Attempts",
            "",
            "| Attempt | Selection | Lanes ran | Would run | Skipped | Estimated seconds saved |",
            "| --- | --- | --- | --- | --- | --- |",
            abandoned
                ? "| control | Abandoned: ChangedSourceFileCount | Alpha lane, Beta lane, Chaos gate | Alpha lane, Beta lane, Chaos gate |  | 0 |"
                : "| control | Resolved | Alpha lane, Beta lane, Chaos gate | Alpha lane, Chaos gate | Beta lane | 23.5 |",
            "",
            "## Input and evidence diagnostics",
            ""
        };
        var legacy = "# Impact replay\n\n" + string.Join(Environment.NewLine, legacyLines) + Environment.NewLine;
        var heading = Environment.NewLine + "## Unmapped test classes";
        var sectionStart = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, "Unmapped test classes section was not written.");
        Assert.Equal(legacy, markdown[..sectionStart]);
        Assert.Equal(Environment.NewLine + string.Join(Environment.NewLine, new[]
        {
            "## Unmapped test classes", "", "- not-scanned: 0", "- no-lane: 0", "- ambiguous: 0", "",
            "No unmapped test classes were found."
        }) + Environment.NewLine, markdown[sectionStart..]);

        var diagnostics = Assert.IsType<ImpactReplayUnmappedTestClasses>(report.UnmappedTestClasses);
        Assert.Equal(3, diagnostics.CountsByReason.Count);
        Assert.All(diagnostics.CountsByReason, item => Assert.Equal(0, item.Value));
        Assert.Empty(diagnostics.TopClasses);
        Assert.Empty(diagnostics.Occurrences);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.OutputDirectory, "impact-replay.json")))!.AsObject();
        Assert.Equal(new[] { "attempts", "attemptsGainingChangedTestClasses", "cleanupErrors", "floorLanes", "floorSource",
            "skippedInputs", "unmappedTestClasses", "unreplayableAttempts" }, json.Select(item => item.Key).Order(StringComparer.Ordinal));
        Assert.Equal(0, json["unmappedTestClasses"]!["countsByReason"]!["not-scanned"]!.GetValue<int>());
        Assert.Equal(0, json["unmappedTestClasses"]!["countsByReason"]!["no-lane"]!.GetValue<int>());
        Assert.Equal(0, json["unmappedTestClasses"]!["countsByReason"]!["ambiguous"]!.GetValue<int>());
        Assert.Empty(json["unmappedTestClasses"]!["topClasses"]!.AsArray());
        Assert.Empty(json["unmappedTestClasses"]!["occurrences"]!.AsArray());
    }
}
