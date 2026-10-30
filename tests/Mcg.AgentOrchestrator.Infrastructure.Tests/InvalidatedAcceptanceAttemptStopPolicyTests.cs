using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class InvalidatedAcceptanceAttemptStopPolicyTests
{
    [Theory]
    [InlineData(119, false)]
    [InlineData(120, false)]
    [InlineData(121, true)]
    public void GraceDeadlineUsesFirstInvalidation(int seconds, bool eligible)
    {
        using var fixture = new Fixture();
        var policy = new InvalidatedAcceptanceAttemptStopPolicy(() => fixture.Now);
        var invalidatedAt = fixture.Attempt.InvalidatedAt;
        fixture.Now = fixture.Now.AddSeconds(seconds);
        fixture.Coordinator.InvalidateCurrent(fixture.Attempt.GoalId, "repeat observation");
        var current = fixture.Read();
        Assert.Equal(invalidatedAt, current.InvalidatedAt);
        var calls = new List<int>();
        var line = policy.Observe(fixture.Coordinator, current, attempt =>
        {
            calls.Add(attempt.OwnerProcessId);
            return new("stopped", [72]);
        });

        Assert.Equal(eligible, line is not null);
        Assert.Equal(eligible ? [Fixture.OwnerPid] : Array.Empty<int>(), calls);
        if (eligible)
        {
            Assert.Contains($"attempt={current.AttemptId} ownerPid={Fixture.OwnerPid} descendantPids=72 ", line);
            Assert.Contains("waitedSeconds=121.000 graceSeconds=120 outcome=stopped", line);
        }
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("failed")]
    [InlineData("refused-identity")]
    [InlineData("throws")]
    public async Task ConcurrentObservationsIssueOneStopEvenAfterLivenessGap(string outcome)
    {
        using var fixture = new Fixture();
        fixture.Now = fixture.Now.AddSeconds(121);
        var policy = new InvalidatedAcceptanceAttemptStopPolicy(() => fixture.Now);
        var calls = 0;
        InvalidatedAcceptanceAttemptStopResult Stop(ConductorParallelAcceptanceAttempt _)
        {
            Interlocked.Increment(ref calls);
            if (outcome == "throws") throw new IOException("fixture stop failure");
            return new(outcome, []);
        }

        var events = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            policy.Observe(fixture.Coordinator, fixture.Attempt, Stop))));
        var stopEvent = Assert.Single(events.OfType<string>());
        Assert.Contains($"outcome={(outcome == "throws" ? "failed" : outcome)}", stopEvent);
        Assert.Equal(1, calls);

        fixture.Alive = false;
        Assert.False(policy.TryHold(fixture.Coordinator, fixture.Attempt.GoalId, Stop, _ => { }, out _));
        fixture.Alive = true;
        var furtherEvents = new List<string>();
        Assert.True(policy.TryHold(fixture.Coordinator, fixture.Attempt.GoalId, Stop, furtherEvents.Add, out _));
        Assert.Empty(furtherEvents);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("image")]
    [InlineData("not-invalidated")]
    [InlineData("exited")]
    public void StaleObservationCannotAuthorizeAnotherOwner(string change)
    {
        using var fixture = new Fixture();
        var observed = fixture.Attempt;
        fixture.Now = fixture.Now.AddSeconds(121);
        var current = change switch
        {
            "pid" => observed with { OwnerProcessId = Fixture.OwnerPid + 1 },
            "start" => observed with { OwnerProcessStartedAt = observed.OwnerProcessStartedAt!.Value.AddSeconds(1) },
            "image" => observed with { OwnerExecutablePath = Path.Combine(fixture.Root, "other.exe") },
            "not-invalidated" => observed with
            {
                Outcome = ConductorParallelAcceptanceAttemptOutcome.Running,
                ReconciledAt = null, CompletedAt = null, InvalidatedAt = null
            },
            _ => observed
        };
        fixture.Write(current);
        if (change == "exited") fixture.Alive = false;
        var calls = 0;
        var policy = new InvalidatedAcceptanceAttemptStopPolicy(() => fixture.Now);

        Assert.Null(policy.Observe(fixture.Coordinator, observed, _ =>
        {
            calls++;
            return new("stopped", []);
        }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("image")]
    [InlineData("missing-start")]
    [InlineData("missing-image")]
    [InlineData("command")]
    [InlineData("inspection")]
    [InlineData("recycled-after-snapshot")]
    public void ProcessStopRefusesUnprovenIdentity(string change)
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt;
        var actual = fixture.Inspection();
        switch (change)
        {
            case "start": actual = actual with { StartedAt = actual.StartedAt!.Value.AddSeconds(1) }; break;
            case "image": actual = actual with { ExecutablePath = Path.Combine(fixture.Root, "other.exe") }; break;
            case "missing-start": attempt = attempt with { OwnerProcessStartedAt = null }; break;
            case "missing-image": attempt = attempt with { OwnerExecutablePath = null }; break;
            case "command": actual = actual with { CommandLine = "dotnet unrelated.dll" }; break;
            case "inspection": actual = actual with { Status = ProcessInspectionStatus.AccessDenied }; break;
        }
        var inspections = 0;
        var kills = new List<int>();
        var stop = new InvalidatedAcceptanceAttemptProcessStop(
            inspect: _ => { inspections++; return actual; },
            descendants: _ =>
            {
                if (change == "recycled-after-snapshot")
                    actual = actual with { StartedAt = actual.StartedAt!.Value.AddSeconds(1) };
                return [72];
            }, stop: pid => { kills.Add(pid); return true; });

        Assert.Equal("refused-identity", stop.Stop(attempt).Outcome);
        Assert.Empty(kills);
        if (change == "recycled-after-snapshot") Assert.Equal(2, inspections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProcessStopUsesOnlyRevalidatedOwnerPid(bool succeeds)
    {
        using var fixture = new Fixture();
        var inspections = new List<int>();
        var treeRequests = new List<int>();
        var kills = new List<int>();
        var stop = new InvalidatedAcceptanceAttemptProcessStop(
            inspect: pid => { inspections.Add(pid); return fixture.Inspection(); },
            descendants: pid => { treeRequests.Add(pid); return [72, 73]; },
            stop: pid => { kills.Add(pid); return succeeds; });

        var result = stop.Stop(fixture.Attempt);

        Assert.Equal(succeeds ? "stopped" : "failed", result.Outcome);
        Assert.Equal([72, 73], result.DescendantPids);
        Assert.Equal([Fixture.OwnerPid, Fixture.OwnerPid], inspections);
        Assert.Equal([Fixture.OwnerPid], treeRequests);
        Assert.Equal([Fixture.OwnerPid], kills);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NaturalExitBeforeStopNeverCallsKill(bool exitAfterTreeSnapshot)
    {
        using var fixture = new Fixture();
        var inspections = 0;
        var kills = 0;
        var stop = new InvalidatedAcceptanceAttemptProcessStop(
            inspect: _ => ++inspections == 1 && exitAfterTreeSnapshot ? fixture.Inspection() : null,
            descendants: _ => [72], stop: _ => { kills++; return true; });

        Assert.Equal("already-exited", stop.Stop(fixture.Attempt).Outcome);
        Assert.Equal(0, kills);
    }

    private sealed class Fixture : IDisposable
    {
        internal const int OwnerPid = 71;
        internal string Root { get; } = Directory.CreateTempSubdirectory("mcg-stop-policy-").FullName;
        internal DateTimeOffset Now = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);
        internal bool Alive = true;
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal ConductorParallelAcceptanceAttempt Attempt { get; }

        internal Fixture()
        {
            var goal = new AgentOrchestratorKernel().CreateGoal("Bound stale acceptance");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
            Coordinator = new(Root, TrunkBranchName.Default, utcNow: () => Now,
                isProcessAlive: _ => Alive, recentHeartbeatGrace: TimeSpan.FromHours(1),
                launchOwnedProcess: _ => new(OwnerPid, Now.AddSeconds(-10), Path.Combine(Root, "dotnet.exe")));
            var started = Coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative,
                (current, _, _, _, _) => ConductorParallelAcceptanceRunResult.Accepted(current,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria));
            Assert.True(Coordinator.InvalidateCurrent(goal.Id.Value, "fixture invalidation"));
            Attempt = Read(started.Attempt.MetadataPath);
            Assert.True(Coordinator.TryGetLiveInvalidatedAttempt(goal.Id.Value, out _));
        }

        internal ConductorParallelAcceptanceAttempt Read() => Read(Attempt.MetadataPath);
        private static ConductorParallelAcceptanceAttempt Read(string path) =>
            JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        internal void Write(ConductorParallelAcceptanceAttempt attempt) =>
            File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt));
        internal ProcessInspectionRecord Inspection() => new(OwnerPid, 1, "dotnet",
            Attempt.OwnerExecutablePath, Attempt.OwnerProcessStartedAt,
            $"dotnet {ConductorParallelAcceptanceAttemptCoordinator.OwnedProcessSubcommandName} {Attempt.MetadataPath}",
            ProcessInspectionStatus.Available);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
