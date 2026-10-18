// Parallel-safe: fixed models; no files, clock or process.
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateLayoutTests
{
    [Fact]
    public void FailingTrain_Collapsed_RendersExactGroupedColumns()
    {
        Assert.Equal(new[]
        {
            "GATE train | elapsed 29m | FAILING",
            "ca798e7c First + 171a09ca Second",
            " x 1 failed   > 4 running   + 4 passed   = 1 cached   . 1 queued",
            "FAILED",
            " x Remainder                          1091s / 970s  local slot 0  failed",
            "     AlphaCase.FailsOnPurpose",
            "     BetaCase.AlsoFails",
            "     GammaCase.Fails",
            "     +1 more",
            "RUNNING (longest pole first)",
            "  lanes",
            " > Process spawning parallel           772s / 555s  mcg-exec-2    [##########]",
            " > Fast lane                           100s / 600s  local slot 1  [#---------]",
            " > Goal worktree cleanup cohort        290s / 573s  mcg-exec-1    [#####-----]",
            "  checks",
            " > provider environment tests           40s / ?     local         [----------]",
            "  confirming",
            " > confirming failure: Infra FullyQ…    60s / ?     local         [----------]",
            "QUEUED",
            " . Mtp managed project rebuild            - / 210s  local",
            "BASELINE  1 comparison runs: 1 passed (Enter to expand)",
            "PASSED  4 lanes, 1 checks (Enter to expand)",
            "WHERE   local 0 [##########]   local 1 [----------]   mcg-exec-1 [----------]  …",
        }, Render(Model(), 80));
    }

    [Fact]
    public void Expanded_PassedAndBaseline_ShowGroupedRows()
    {
        var lines = Render(Model(), 120, true);
        var baseline = Array.FindIndex(lines, line => line.StartsWith("BASELINE"));
        var passed = Array.FindIndex(lines, line => line.StartsWith("PASSED"));
        Assert.Equal("BASELINE  1 comparison runs: 1 passed (Enter to collapse)", lines[baseline]);
        Assert.StartsWith(" + cli tests [trusted main baseline]", lines[baseline + 1]);
        Assert.Equal("PASSED  4 lanes, 1 checks (Enter to collapse)", lines[passed]);
        Assert.Equal("  lanes", lines[passed + 1]);
        Assert.StartsWith(" + Passed A", lines[passed + 2]);
        Assert.StartsWith(" + Passed B", lines[passed + 3]);
        Assert.StartsWith(" + Passed C", lines[passed + 4]);
        Assert.StartsWith(" = Cached", lines[passed + 5]);
        Assert.Equal("  checks", lines[passed + 6]);
        Assert.StartsWith(" + cli tests ", lines[passed + 7]);
    }

    [Fact]
    public void PassingGate_ShowsProgressAndNoFailedSection()
    {
        var gate = Model() with { Lanes = Model().Lanes.Where(row => row.Outcome != "failed").ToArray() };
        var lines = Render(gate);
        Assert.DoesNotContain("FAILED", lines);
        Assert.Equal(OwnerConsoleGateFormatter.ProgressLine(gate), lines[3]);
        Assert.EndsWith("~9m", OwnerConsoleGateFormatter.StatusSegment(gate));
        Assert.EndsWith("FAILING", OwnerConsoleGateFormatter.StatusSegment(Model()));
    }

    [Fact]
    public void BaselineStates_AreExcludedFromOrdinarySectionsAndCounts()
    {
        var gate = Model();
        var changed = gate with { Lanes = gate.Lanes.Concat(new[]
        {
            Lane("core tests [trusted main baseline]", OwnerConsoleGateLaneState.Done, "failed", manifest: false),
            Lane("execution tests [trusted main baseline]", OwnerConsoleGateLaneState.Running, manifest: false)
        }).ToArray() };
        var before = Render(gate, 120);
        var collapsed = Render(changed, 120);
        Assert.Equal(before[0], collapsed[0]);
        Assert.Equal(before[2], collapsed[2]);
        Assert.Contains("BASELINE  3 comparison runs: 1 failed, 1 running, 1 passed (Enter to expand)", collapsed);
        Assert.DoesNotContain(collapsed, line => line.Contains("core tests") || line.Contains("execution tests"));
        var expanded = Render(changed, 120, true);
        var index = Array.FindIndex(expanded, line => line.StartsWith("BASELINE"));
        Assert.StartsWith(" x core tests", expanded[index + 1]);
        Assert.StartsWith(" > execution tests", expanded[index + 2]);
        Assert.StartsWith(" + cli tests [trusted main baseline]", expanded[index + 3]);
    }

    [Fact]
    public void KindPrecedence_AttributionThenBaselineThenManifest_DeterminesGrouping()
    {
        var gate = Model() with { Lanes = new[]
        {
            Lane("x [trusted main baseline]", OwnerConsoleGateLaneState.Done, "passed"),
            Lane("x [trusted main baseline]", OwnerConsoleGateLaneState.Running, manifest: true) with
                { IsAttribution = true, Target = "reviewer focused evidence: Signal" },
            Lane("unrelated check", OwnerConsoleGateLaneState.Running, manifest: false),
            Lane("done lane", OwnerConsoleGateLaneState.Done, "passed")
        } };
        var lines = Render(gate, 120, true);
        Assert.Equal(" x 0 failed   > 1 running   + 1 passed   = 0 cached   . 0 queued", lines[2]);
        Assert.Contains("  checks", lines);
        Assert.Contains("  confirming", lines);
        Assert.Contains(lines, line => line.StartsWith(" > confirming failure: Signal"));
        Assert.Contains(lines, line => line.StartsWith(" + x [trusted main baseline]"));
        Assert.Contains("BASELINE  1 comparison runs: 1 passed (Enter to collapse)", lines);
        Assert.Contains("PASSED  1 lanes (Enter to collapse)", lines);
    }

    [Fact]
    public void OnlyPassedLanes_OmitsEmptySectionsAndSubheaders()
    {
        var gate = Model() with { Lanes = [Lane("one lane", OwnerConsoleGateLaneState.Done, "passed")] };
        var lines = Render(gate);
        Assert.Equal(new[] { "GATE train | elapsed 29m", "ca798e7c First + 171a09ca Second",
            " x 0 failed   > 0 running   + 1 passed   = 0 cached   . 0 queued",
            "[######----] 62%  ~9m left", "PASSED  1 lanes (Enter to expand)", "WHERE   local [##########]" }, lines);
        Assert.DoesNotContain(Render(gate, 120, true), line => line.StartsWith("  "));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void ExpandedRows_ShareTimeLocationOutcomeAndElapsedEndColumns(int width)
    {
        AssertColumns(Render(Model(), width, true));
    }

    [Fact]
    public void NamesWithinCap_AtWidth120_RemainUncutAcrossExpandedSections()
    {
        var gate = Model();
        var lines = Render(gate, 120, true);
        var rows = gate.Lanes.Select(row => (Row: row, Name: row.IsAttribution
            ? OwnerConsoleGateFormatter.AttributionName(row) : row.Name)).Where(item => item.Name.Length <= 33).ToArray();
        Assert.NotEmpty(rows);
        foreach (var (row, name) in rows)
        {
            var prefix = $" {OwnerConsoleGateFormatter.Marker(row)} {name.PadRight(33)}  ";
            var line = Assert.Single(lines, line => line.StartsWith(prefix, StringComparison.Ordinal));
            Assert.Equal(name, line.Substring(3, 33).TrimEnd());
            Assert.DoesNotContain('…', line.Substring(3, 33));
        }
        AssertColumns(lines);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void LongNames_RespectThirtyThreeCharacterCapAndPreserveOtherColumns(int width)
    {
        var gate = Model() with { Lanes = Model().Lanes.Select((row, index) =>
            index == 2 ? row with { Name = new string('N', 60) } : row).ToArray() };
        var lines = Render(gate, width, true);
        Assert.Contains(lines, line => line.Contains(new string('N', 32) + "…"));
        Assert.Contains(lines, line => line.Contains("100s / 600s  local slot 1  [#---------]"));
        AssertColumns(lines);
        Assert.Contains(lines, line => line.Contains("Goal worktree cleanup cohort "));
    }

    [Fact]
    public void HostActivity_LiveIdleAndEmpty_RenderSnapshotAges()
    {
        var gate = Model();
        var other = gate with { Kind = "cohort", Members = [new("aaaaaaaa1", ""), new("bbbbbbbb2", "")], Elapsed = TimeSpan.FromMinutes(7) };
        var activity = new OwnerConsoleHostActivity("c8cfc17efull", "SampleTests, +2", DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(5));
        var lines = Render(gate with { OtherLiveGates = [other], HostActivity = [activity] }, 120);
        Assert.Equal("ALSO ON HOST  gate cohort aaaaaaaa+bbbbbbbb 7m", lines[^2]);
        Assert.Equal("ALSO ON HOST  c8cfc17e evidence (SampleTests, +2) 5m", lines[^1]);
        Assert.Equal(new[] { "gate: idle", lines[^1] }, Render(OwnerConsoleGateProgress.Idle() with { HostActivity = [activity] }, 120));
        Assert.DoesNotContain(Render(gate), line => line.StartsWith("ALSO ON HOST"));
        Assert.Equal(new[] { "gate: unavailable" }, OwnerConsoleGateLayout.Lines(null, 80, false));
    }

    [Fact]
    public void UnknownOutcomesQuietBoundaryAndDataCharacters_AreRenderedSafely()
    {
        var gate = Model() with { Lanes =
        [
            Lane("no outcome", OwnerConsoleGateLaneState.Done),
            Lane("café\nname", OwnerConsoleGateLaneState.Running, elapsed: 20, median: null) with { QuietSeconds = 301 }
        ] };
        var lines = Render(gate, 120);
        Assert.Contains("DONE  1 without a recorded outcome", lines);
        Assert.Contains(lines, line => line.Contains("caf??name") && line.EndsWith("[----------] quiet 5m"));
        Assert.DoesNotContain(Render(gate with { Lanes = [gate.Lanes[1] with { QuietSeconds = 300 }] }, 120),
            line => line.Contains("quiet"));
        Assert.Equal(new[] { "…" }, OwnerConsoleGateLayout.Lines(null, 1, false));
    }

    private static string[] Render(OwnerConsoleGateProgress gate, int width = 100, bool expanded = false)
    {
        var lines = OwnerConsoleGateLayout.Lines(gate, width, expanded).ToArray();
        Assert.All(lines, line =>
        {
            Assert.True(line.Length <= width, line);
            Assert.All(line, c => Assert.True(c is >= ' ' and <= '~' || c == '…', line));
        });
        return lines;
    }

    private static void AssertColumns(IReadOnlyList<string> lines)
    {
        var rows = lines.Where(line => line.Length > 3 && line[0] == ' ' && "x>+.=".Contains(line[1]) &&
            line.Contains(" / ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(rows);
        var slash = rows[0].IndexOf(" / ", StringComparison.Ordinal);
        // Five-wide elapsed, four-wide median, twelve-wide location in this fixture.
        Assert.All(rows, row => Assert.Equal(slash, row.IndexOf(" / ", StringComparison.Ordinal)));
        foreach (var row in rows.Where(row => row[1] != '.'))
        {
            Assert.Equal('s', row[slash - 1]);
            var location = row.Substring(slash + 9, 12).Trim();
            Assert.Contains(location, new[] { "local", "local slot 0", "local slot 1", "mcg-exec-1", "mcg-exec-2" });
            Assert.Contains(row[(slash + 23)..], new[] { "failed", "passed", "cached", "[##########]", "[#---------]", "[#####-----]", "[----------]" });
        }
    }

    private static OwnerConsoleGateProgress Model() => new(true, "train",
        [new("ca798e7c", "First"), new("171a09ca", "Second")], DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(29),
        [
            Lane("Remainder", OwnerConsoleGateLaneState.Done, "failed", 1091, 970, "local slot 0") with
                { FailingTestNames = ["AlphaCase.FailsOnPurpose", "BetaCase.AlsoFails", "GammaCase.Fails", "DeltaCase.Fails"] },
            Lane("confirmation", OwnerConsoleGateLaneState.Running, elapsed: 60, median: null, manifest: false) with
                { IsAttribution = true, Target = "reviewer focused evidence: Infra FullyQualifiedName~Fails" },
            Lane("Fast lane", OwnerConsoleGateLaneState.Running, elapsed: 100, median: 600, location: "local slot 1"),
            Lane("Goal worktree cleanup cohort", OwnerConsoleGateLaneState.Running, elapsed: 290, median: 573, location: "mcg-exec-1"),
            Lane("Process spawning parallel", OwnerConsoleGateLaneState.Running, elapsed: 772, median: 555, location: "mcg-exec-2"),
            Lane("provider environment tests", OwnerConsoleGateLaneState.Running, elapsed: 40, median: null, manifest: false),
            Lane("Mtp managed project rebuild", OwnerConsoleGateLaneState.Queued, median: 210),
            Lane("Passed A", OwnerConsoleGateLaneState.Done, "passed"),
            Lane("Passed B", OwnerConsoleGateLaneState.Done, "passed"),
            Lane("Passed C", OwnerConsoleGateLaneState.Done, "passed"),
            Lane("Cached", OwnerConsoleGateLaneState.Cached, "passed"),
            Lane("cli tests", OwnerConsoleGateLaneState.Done, "passed", manifest: false),
            Lane("cli tests [trusted main baseline]", OwnerConsoleGateLaneState.Done, "passed", manifest: false)
        ], .62, 540, 5, 10);

    private static OwnerConsoleGateLane Lane(string name, OwnerConsoleGateLaneState state, string outcome = "unknown",
        int elapsed = 0, double? median = 100, string location = "local", bool manifest = true) =>
        new(name, state, outcome, location, TimeSpan.FromSeconds(elapsed), median, false, IsManifestLane: manifest);
}
