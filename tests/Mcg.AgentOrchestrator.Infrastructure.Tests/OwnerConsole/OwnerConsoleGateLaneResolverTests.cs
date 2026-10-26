using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its resolver, history and explicit clock.
public sealed class OwnerConsoleGateLaneResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 5, 0, 0, TimeSpan.Zero);

    private static OwnerConsoleGateManifestLane[] Manifest() =>
    [
        new("Remainder", 10), new("Remainder balance A", 20),
        new("Process spawning", 30), new("Process spawning host-bound", 40),
        new("P", 50), new("P: q", 60)
    ];

    public static IEnumerable<object[]> Targets()
    {
        foreach (var lane in Manifest())
        {
            yield return [lane.Name, lane.Name];
            yield return ["infrastructure tests: " + lane.Name, lane.Name];
            yield return [lane.Name + ": shard", lane.Name];
            yield return ["infrastructure tests: " + lane.Name + ": shard", lane.Name];
        }
        yield return ["P: q: r", "P: q"];
        yield return ["PQ", "PQ"];
        yield return ["core tests", "core tests"];
        yield return ["remainder", "remainder"];
        yield return ["", ""];
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Resolve_NestedAndPrefixedTargets_MatchesOracle(string target, string expected)
    {
        var manifest = Manifest();
        var resolver = new OwnerConsoleGateLaneResolver(manifest);

        Assert.Equal(expected, MatchLane(target, manifest));
        Assert.Equal(MatchLane(target, manifest), resolver.Resolve(target));
        Assert.Equal(expected, resolver.Resolve(target));
        Assert.Equal(1, resolver.Computations);
        Assert.Equal(new[] { target }, resolver.ComputedTargets);
    }

    [Theory]
    [InlineData(false, "P")]
    [InlineData(true, "infrastructure tests: P")]
    public void Resolve_ExactKeyCollision_FirstManifestLaneWins(bool reverse, string expected)
    {
        OwnerConsoleGateManifestLane[] manifest = reverse
            ? [new("infrastructure tests: P", 2), new("P", 1), new("P", 3)]
            : [new("P", 1), new("infrastructure tests: P", 2), new("P", 3)];
        var resolver = new OwnerConsoleGateLaneResolver(manifest);

        Assert.Equal(expected, resolver.Resolve("infrastructure tests: P"));
        Assert.Equal(MatchLane("infrastructure tests: P", manifest), resolver.Resolve("infrastructure tests: P"));
    }

    [Fact]
    public void Build_TwoLiveGates_ComputesEachDistinctTargetOnce()
    {
        var manifest = Manifest();
        var resolver = new OwnerConsoleGateLaneResolver(manifest);
        string[] historyNames = ["Remainder", "infrastructure tests: Remainder balance A",
            "Process spawning: shard", "Process spawning host-bound", "core tests", "retired lane"];
        var history = Enumerable.Range(0, 2000)
            .Select(i => new OwnerConsoleGateDuration(historyNames[i % 6], i + 1, Now.AddSeconds(-i))).ToArray();
        string[] eventTargets = ["P: q: r", "infrastructure tests: P: shard", "Remainder: remote", "core tests"];
        var events = new List<OwnerConductEvent>();
        foreach (var goal in new[] { "11111111", "22222222" })
        {
            events.Add(Event(-100, "acceptance", "ACCEPTANCE result=started", goal));
            events.Add(Progress(-90, "verification-check", eventTargets[0], goal));
            events.Add(Progress(-80, "shard-complete", eventTargets[1], goal));
            events.Add(Event(-70, "remote-lane", $"REMOTE_LANE_POLICY lane=\"{eventTargets[2]}\" decision=offer", goal));
            events.Add(Event(-60, "remote-lane", $"REMOTE_LANE_POLICY lane=\"{eventTargets[3]}\" decision=offer", goal));
            events.Add(Progress(-50, "gate-phase-breakdown", "ignored non-lane target", goal));
        }

        var board = OwnerConsoleGateProgress.Build(Now, events, manifest, history, lanes: resolver);

        Assert.True(board.Live);
        var other = Assert.Single(board.OtherLiveGates!);
        Assert.Equal("11111111", Assert.Single(board.Members).Id);
        Assert.Equal("22222222", Assert.Single(other.Members).Id);
        Assert.Equal(resolver.ComputedTargets.Count, resolver.ComputedTargets.Distinct(StringComparer.Ordinal).Count());
        Assert.True(resolver.Computations <= historyNames.Distinct().Count() + eventTargets.Distinct().Count());
        Assert.Equal(historyNames.Concat(eventTargets).Distinct(StringComparer.Ordinal).Order(),
            resolver.ComputedTargets.Order());
        Assert.Equal(historyNames, resolver.ComputedTargets.Take(6));
    }

    [Fact]
    public void Build_StableNewestTenAndMixedRows_PreservesLiteralOutput()
    {
        var history = Enumerable.Range(1, 9)
            .Select(i => new OwnerConsoleGateDuration("Remainder", i * 10, Now.AddMinutes(-i))).ToList();
        // The tenth slot takes 1 before 1000 at the tie: median 45, rather than 55.
        history.AddRange([
            new("Remainder", 1, Now.AddMinutes(-10)), new("Remainder", 1000, Now.AddMinutes(-10)),
            new("infrastructure tests: Process spawning", 20, Now.AddMinutes(-2)),
            new("Process spawning: shard", 40, Now.AddMinutes(-1)),
            new("core tests", 20, Now.AddMinutes(-2)), new("core tests", 30, Now.AddMinutes(-1)),
            new("retired lane", 9999, Now), new("P", double.NaN, Now), new("P", 0, Now)
        ]);
        OwnerConductEvent[] events =
        [
            Event(-100, "acceptance", "ACCEPTANCE result=started"),
            Progress(-50, "check-complete", "infrastructure tests: Remainder", extra: " elapsed_ms=45000 outcome=passed"),
            Progress(-40, "shard-complete", "Process spawning: shard", extra: " elapsed_ms=30000 outcome=passed"),
            Progress(-30, "cache-hit", "Process spawning host-bound"),
            Progress(-5, "verification-check", "core tests", extra: " heartbeat=build-slots/activity-2.heartbeat.json"),
            Progress(-4, "verification-check", "PQ")
        ];

        var board = OwnerConsoleGateProgress.Build(Now, events, Manifest(), history);

        Assert.Collection(board.Lanes,
            row => AssertRow(row, "Remainder", OwnerConsoleGateLaneState.Done, 45, false, true, "local", 45),
            row => AssertRow(row, "Remainder balance A", OwnerConsoleGateLaneState.Queued, 20, true, true, "local", 0),
            row => AssertRow(row, "Process spawning", OwnerConsoleGateLaneState.Done, 30, false, true, "local", 30),
            row => AssertRow(row, "Process spawning host-bound", OwnerConsoleGateLaneState.Cached, 40, true, true, "local", 0),
            row => AssertRow(row, "P", OwnerConsoleGateLaneState.Queued, 50, true, true, "local", 0),
            row => AssertRow(row, "P: q", OwnerConsoleGateLaneState.Queued, 60, true, true, "local", 0),
            row => AssertRow(row, "core tests", OwnerConsoleGateLaneState.Running, 25, false, false, "local slot 2", 5),
            row => AssertRow(row, "PQ", OwnerConsoleGateLaneState.Running, null, true, false, "local", 4));
        // Serial remaining: 20 + 50 + 60 + (25 - 5) = 150; fraction = 100 / 250.
        Assert.Equal(0.4, board.Fraction);
        Assert.Equal(150, board.RemainingSeconds);
        Assert.Equal(3, board.DoneCount);
        Assert.Equal(6, board.TotalCount);
    }

    [Fact]
    public void Build_ChangedManifest_DoesNotReusePreviousMemo()
    {
        OwnerConductEvent[] events = [Event(-10, "acceptance", "ACCEPTANCE result=started"),
            Progress(-5, "check-complete", "X: shard")];
        OwnerConsoleGateDuration[] history = [new("X: shard", 12, Now)];

        var first = OwnerConsoleGateProgress.Build(Now, events, [new("X", 100)], history);
        var second = OwnerConsoleGateProgress.Build(Now, events, [new("Y", 200)], history);

        var firstRow = Assert.Single(first.Lanes);
        Assert.Equal("X", firstRow.Name);
        Assert.Equal(12d, firstRow.MedianSeconds);
        Assert.False(firstRow.IsEstimate);
        Assert.DoesNotContain(second.Lanes, row => row.Name == "X");
        var manifestRow = Assert.Single(second.Lanes, row => row.IsManifestLane);
        Assert.Equal("Y", manifestRow.Name);
        Assert.Equal(200d, manifestRow.MedianSeconds);
        Assert.True(manifestRow.IsEstimate);
        // A stale memo would create an X row here instead of the unmatched target.
        var secondRow = Assert.Single(second.Lanes, row => !row.IsManifestLane);
        Assert.Equal("X: shard", secondRow.Name);
        Assert.Equal(12d, secondRow.MedianSeconds);
        Assert.False(secondRow.IsEstimate);
    }

    private static void AssertRow(OwnerConsoleGateLane row, string name, OwnerConsoleGateLaneState state,
        double? median, bool estimate, bool manifest, string location, double elapsed)
    {
        Assert.Equal(name, row.Name);
        Assert.Equal(state, row.State);
        Assert.Equal(median, row.MedianSeconds);
        Assert.Equal(estimate, row.IsEstimate);
        Assert.Equal(manifest, row.IsManifestLane);
        Assert.Equal(location, row.Location);
        Assert.Equal(TimeSpan.FromSeconds(elapsed), row.Elapsed);
    }

    private static OwnerConductEvent Event(int seconds, string kind, string detail, string goal = "11111111") =>
        new(Now.AddSeconds(seconds), kind, goal, detail);

    private static OwnerConductEvent Progress(int seconds, string phase, string target,
        string goal = "11111111", string extra = "") =>
        Event(seconds, "gate-progress", $"ACCEPTANCE_PROGRESS phase={phase} target=\"{target}\"{extra}", goal);

    private static string MatchLane(string target, IReadOnlyList<OwnerConsoleGateManifestLane> manifest) =>
        manifest.FirstOrDefault(lane => target == lane.Name || target == "infrastructure tests: " + lane.Name)?.Name ??
        manifest.OrderByDescending(lane => lane.Name.Length).FirstOrDefault(lane =>
            target.StartsWith(lane.Name + ":", StringComparison.Ordinal) ||
            target.StartsWith("infrastructure tests: " + lane.Name + ":", StringComparison.Ordinal))?.Name ?? target;
}
