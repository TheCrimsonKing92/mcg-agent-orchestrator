// Parallel-safe: fixed events and a fixed now; no files, clock or process.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateOwnerScreenshotScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Checks = ["core tests", "cli tests", "execution tests", "provider environment tests",
        "acceptance execution owner tests", "real process shard probe"];

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void OwnerTrain_RealEvents_CollapsesKindsAndExpandsIntoAlignedColumns(int width)
    {
        var gate = Build();
        Assert.True(gate.Live);
        Assert.False(gate.Failing);
        Assert.Equal(33, gate.Lanes.Count);
        Assert.Equal(20, gate.Lanes.Count(row => row.IsManifestLane && row.Outcome == "passed"));
        Assert.Equal(new[] { "local slot 0", "mcg-exec-1", "mcg-exec-2" }, gate.Lanes.Take(3).Select(row => row.Location));
        var collapsed = Render(gate, width, false);
        AssertCollapsed(gate, collapsed);
        AssertCollapsed(gate, OwnerConsoleGateFormatter.Lines(gate));

        var expanded = Render(gate, width, true);
        var baseline = Array.FindIndex(expanded, line => line.StartsWith("BASELINE"));
        var passed = Array.FindIndex(expanded, line => line.StartsWith("PASSED"));
        Assert.Equal("BASELINE  6 comparison runs: 6 passed (Enter to collapse)", expanded[baseline]);
        Assert.Equal("PASSED  20 lanes, 6 checks (Enter to collapse)", expanded[passed]);
        Assert.Equal(7, passed - baseline);
        Assert.Equal("  lanes", expanded[passed + 1]);
        Assert.Equal("  checks", expanded[passed + 22]);
        Assert.Equal(6, expanded.Skip(passed + 23).TakeWhile(line => line.StartsWith(" + ")).Count());
        var rows = expanded.Where(IsRow).ToArray();
        Assert.Equal(33, rows.Length);
        var slash = rows[0].IndexOf(" / ", StringComparison.Ordinal);
        var printed = new[] { gate.Lanes.Single(row => row.Name == "Remainder") }
            .Concat(gate.Lanes.Where(row => row.Name.EndsWith(" [trusted main baseline]")))
            .Concat(gate.Lanes.Where(row => row.IsManifestLane && row.Name != "Remainder"))
            .Concat(gate.Lanes.Where(row => !row.IsManifestLane && !row.Name.EndsWith(" [trusted main baseline]"))).ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var source = printed[i];
            Assert.Equal(slash, row.IndexOf(" / ", StringComparison.Ordinal));
            Assert.Equal('s', row[slash - 1]);
            Assert.Equal(source.Location, row.Substring(slash + 9, 12).Trim());
            Assert.Equal(source.State == OwnerConsoleGateLaneState.Running ? "[##########]" : "passed", row[(slash + 23)..]);
            var nameColumn = row.Substring(3, 33).TrimEnd();
            Assert.Equal(source.Name.Length <= 33 ? source.Name : source.Name[..32] + "…", nameColumn);
            Assert.DoesNotContain(source.Location, nameColumn);
            if (source.Name.Length <= 33) Assert.DoesNotContain('…', nameColumn);
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void LongLaneName_RealEvents_CutsOnlyNameAndPreservesColumnAlignment(int width)
    {
        var gate = Build(longName: true);
        var lines = Render(gate, width, true);
        var longRow = Assert.Single(lines, line => line.StartsWith(" + " + new string('N', 32)));
        Assert.Contains(new string('N', 32) + "…", longRow);
        Assert.Contains("100s / 600s  local slot 0  passed", longRow);
        var slash = longRow.IndexOf(" / ", StringComparison.Ordinal);
        Assert.All(lines.Where(IsRow), row => Assert.Equal(slash, row.IndexOf(" / ", StringComparison.Ordinal)));
        Assert.Contains(lines, line => line.StartsWith(" + Lane 02") && line.EndsWith("mcg-exec-1    passed"));
    }

    private static void AssertCollapsed(OwnerConsoleGateProgress gate, IReadOnlyList<string> lines)
    {
        Assert.Equal("GATE train | elapsed 30m", lines[0]);
        Assert.Equal("ca798e7c First + 171a09ca Second", lines[1]);
        Assert.Equal(" x 0 failed   > 1 running   + 26 passed   = 0 cached   . 0 queued", lines[2]);
        Assert.Equal(OwnerConsoleGateFormatter.ProgressLine(gate), lines[3]);
        Assert.Equal("RUNNING (longest pole first)", lines[4]);
        Assert.StartsWith(" > Remainder ", lines[5]);
        Assert.Equal("BASELINE  6 comparison runs: 6 passed (Enter to expand)", lines[6]);
        Assert.Equal("PASSED  20 lanes, 6 checks (Enter to expand)", lines[7]);
        Assert.StartsWith("WHERE   ", lines[8]);
        Assert.Equal(9, lines.Count);
        Assert.Single(lines.Where(IsRow));
        Assert.DoesNotContain(lines, line => line.Contains("[trusted main baseline]"));
        Assert.DoesNotContain(lines, line => line.StartsWith("  "));
    }

    private static string[] Render(OwnerConsoleGateProgress gate, int width, bool expanded)
    {
        var lines = OwnerConsoleGateLayout.Lines(gate, width, expanded).ToArray();
        Assert.All(lines, line =>
        {
            Assert.True(line.Length <= width, line);
            Assert.All(line, c => Assert.True(c is >= ' ' and <= '~' || c == '…', line));
        });
        return lines;
    }

    private static bool IsRow(string line) => line.StartsWith(" > ") || line.StartsWith(" + ");

    private static OwnerConsoleGateProgress Build(bool longName = false)
    {
        var names = Enumerable.Range(1, 20).Select(i => i == 1 && longName ? new string('N', 60) :
            i == 20 ? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg" : $"Lane {i:00}").ToArray();
        var manifest = names.Select(name => new OwnerConsoleGateManifestLane(name, 600)).Append(new("Remainder", 970)).ToArray();
        var events = new List<OwnerConductEvent>
        {
            Event(-1800, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=ca798e7c,171a09ca train=owner")
        };
        for (var i = 0; i < names.Length; i++)
        {
            var remote = i % 3 != 0;
            var phase = remote ? "remote-shard-complete" : "shard-complete";
            var extra = remote ? " executor=mcg-exec-" + (i % 3) : " heartbeat=activity-0.heartbeat.json";
            events.Add(Event(-30, "gate-progress", $"PHASE_PROGRESS goal=ca798e7c phase={phase} target=\"infrastructure tests: {names[i]}\" elapsed_ms={(100 + i) * 1000} outcome=passed" + extra));
        }
        foreach (var name in Checks.SelectMany(check => new[] { check, check + " [trusted main baseline]" }))
            events.Add(Event(-20, "gate-progress", $"PHASE_PROGRESS goal=ca798e7c phase=check-complete target=\"{name}\" elapsed_ms=30000 outcome=passed"));
        events.Add(Event(-60, "gate-progress", "PHASE_PROGRESS goal=ca798e7c phase=verification-check target=\"infrastructure tests: Remainder\" elapsed_ms=1031000 heartbeat=activity-0.heartbeat.json"));
        return OwnerConsoleGateProgress.Build(Now, events, manifest, [], id => id == "ca798e7c" ? "First" : "Second");
    }

    private static OwnerConductEvent Event(int seconds, string kind, string detail) => OwnerConsoleGateProgress.ParseEvent(
        JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(seconds), eventKind = kind, goalId = "ca798e7c", detail }))!;
}
