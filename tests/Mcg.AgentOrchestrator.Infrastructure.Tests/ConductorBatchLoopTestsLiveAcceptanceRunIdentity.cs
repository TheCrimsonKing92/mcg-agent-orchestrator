using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLiveAcceptanceRunIdentity : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLiveAcceptanceRunIdentity(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void CohortShardHeartbeatsOccupyOneSlotAndAdmitSecondRunAtWidthTwo()
    {
        var memberIds = new[] { new GoalId(new string('a', 32)), new GoalId(new string('b', 32)) };
        var key = ConductorDriver.CohortGateRunIdentity(memberIds.Select(id => id.Value));
        var root = new ConductorAcceptanceCapacityRoot(key,
            memberIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal));
        var gates = Enumerable.Range(5001, 4).Select(pid => Gate(pid, key)).ToArray();

        var census = Census(gates, [root]);

        Assert.Equal(1, census.OccupiedCount);
        Assert.Equal("live acceptance occupants goal:aaaaaaaa", census.Describe());
        Assert.True(ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, width: 2).IsAdmitted);
    }

    [Xunit.Fact]
    public void SoloAttemptHeartbeatsWithAttemptRunIdCountOnce()
    {
        var attempt = Attempt();
        var gates = Enumerable.Range(4201, 3)
            .Select(pid => Gate(pid, attempt.AttemptId, attempt.GoalId)).ToArray();

        var census = ConductorBatchLoop.BuildLiveAcceptanceCensus(
            [attempt], new HashSet<string>([attempt.AttemptId], StringComparer.Ordinal),
            new ConductorAcceptanceCapacitySnapshot([]), gates);

        Assert.Equal(1, census.OccupiedCount);
        Assert.Equal("live acceptance occupants goal:abcdef12", census.Describe());
    }

    [Xunit.Fact]
    public void SoloRunClaimAlsoCoversHeartbeatsWithoutGoalOrOwnerPid()
    {
        var attempt = Attempt();
        var census = ConductorBatchLoop.BuildLiveAcceptanceCensus(
            [attempt], new HashSet<string>([attempt.AttemptId], StringComparer.Ordinal),
            new ConductorAcceptanceCapacitySnapshot([]), [Gate(4201, attempt.AttemptId)]);

        Assert.Equal(1, census.OccupiedCount);
    }

    [Xunit.Fact]
    public void ReservedAttemptWithoutMetadataStillClaimsItsRun()
    {
        var census = ConductorBatchLoop.BuildLiveAcceptanceCensus(
            [], new HashSet<string>(["attempt-1"], StringComparer.Ordinal),
            new ConductorAcceptanceCapacitySnapshot([]), [Gate(4201, "attempt-1")]);

        Assert.Equal(1, census.OccupiedCount);
        Assert.Equal("live acceptance occupants attempt:attempt-", census.Describe());
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData("   ")]
    public void IdentitylessUnclaimedAcceptanceHeartbeatStillCounts(string? runId)
    {
        var census = Census([Gate(4300, runId)]);

        Assert.Equal(1, census.OccupiedCount);
        Assert.Equal("live acceptance occupants pid:4300", census.Describe());
    }

    [Xunit.Fact]
    public void UnclaimedRunDoesNotCollapseDistinctShardProcesses()
    {
        var census = Census([Gate(4301, "unknown-run"), Gate(4302, "unknown-run")]);

        Assert.Equal(2, census.OccupiedCount);
        Assert.False(ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, 2).IsAdmitted);
    }

    [Xunit.Fact]
    public void UnclaimedForeignGoalShards_CountOnceAndAdmitAtWidthTwo()
    {
        var census = Census([
            Gate(4601, "run-a", "231f9591-foreign"),
            Gate(4602, "run-a", "231f9591-foreign"),
            Gate(4603, "run-a", "231f9591-foreign")]);

        Assert.Equal(1, census.OccupiedCount);
        Assert.Equal("live acceptance occupants goal:231f9591", census.Describe());
        Assert.True(ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, 2).IsAdmitted);
    }

    [Xunit.Fact]
    public void DistinctForeignGoalsWithSamePrefix_FillWidthTwo()
    {
        var census = Census([
            Gate(4611, "run-a", "231f9591-foreign-one"),
            Gate(4612, "run-a", "231f9591-foreign-two")]);

        Assert.Equal(2, census.OccupiedCount);
        Assert.False(ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, 2).IsAdmitted);
    }

    [Xunit.Fact]
    public void UnclaimedGoalIdentity_TrimsAndIgnoresCaseAcrossRuns()
    {
        var census = Census([
            Gate(4621, "run-a", "231f9591-foreign"),
            Gate(4622, "run-b", " 231F9591-FOREIGN ")]);

        Assert.Equal(1, census.OccupiedCount);
    }

    [Xunit.Fact]
    public void EmptyAndWhitespaceGoalIds_KeepDistinctProcessKeys()
    {
        var census = Census([Gate(4631, "run-a", ""), Gate(4632, "run-a", "   ")]);

        Assert.Equal(2, census.OccupiedCount);
        Assert.Equal("live acceptance occupants pid:4631,pid:4632", census.Describe());
    }

    [Xunit.Theory]
    [Xunit.InlineData("train:aaaa+bbbb", 1)]
    [Xunit.InlineData(" TRAIN:AAAA+BBBB ", 1)]
    [Xunit.InlineData("train:aaaa+bbbb-extra", 2)]
    public void TrainRootClaimsOnlyItsExactRunIdentity(string runId, int expectedCount)
    {
        var root = new ConductorAcceptanceCapacityRoot("train:aaaa+bbbb",
            new HashSet<string>(["aaaa", "bbbb"], StringComparer.Ordinal));

        Assert.Equal(expectedCount, Census([Gate(4400, runId)], [root]).OccupiedCount);
    }

    [Xunit.Fact]
    public void FocusedEvidenceWithUnclaimedRunRemainsExcluded()
    {
        var gate = Gate(4500, "unknown-run") with { RunClass = GateHeartbeatRunClass.FocusedEvidence };

        Assert.Equal(0, Census([gate]).OccupiedCount);
    }

    [Xunit.Fact]
    public void CohortHeartbeatIdentityEqualsRegisteredCapacityRootKey()
    {
        var first = new GoalId(new string('b', 32));
        var second = new GoalId(new string('a', 32));
        var selection = new ConductorAcceptanceCohortSelection([Projection(first), Projection(second)], []);
        var driver = MakeDriver();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.True(driver.TryRegisterCohortGateRunForTests(selection, completion));
            Assert.Equal(ConductorDriver.CohortGateRunIdentity([first.Value, second.Value]),
                Assert.Single(driver.GetActiveAcceptanceCohortCapacity().ActiveRoots).Key);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    [Xunit.Fact]
    public void Census_SixForeignProjectGatesAndOneOwnGate_CountsOnlyOwnGate()
    {
        var ownDirectory = Path.Combine(Path.GetTempPath(), $"mcg-own-{Guid.NewGuid():N}");
        var foreignDirectory = Path.Combine(Path.GetTempPath(), $"mcg-foreign-{Guid.NewGuid():N}");
        using var owner = GateHeartbeatOwner.Push(ownDirectory);
        var gates = Enumerable.Range(4701, 6)
            .Select(pid => Gate(pid, "foreign-train-run") with { OwnerStateDirectory = foreignDirectory })
            .Append(Gate(4800, "own-run") with { OwnerStateDirectory = ownDirectory }).ToArray();

        var census = Census(gates);

        Assert.Equal(1, census.OccupiedCount);
        Assert.Equal("live acceptance occupants pid:4800", census.Describe());
    }

    [Xunit.Theory]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("own")]
    [Xunit.InlineData("unbound")]
    public void Census_OwnOrUnknownProject_KeepsBaselineCounting(string scenario)
    {
        var ownDirectory = Path.Combine(Path.GetTempPath(), $"mcg-own-{Guid.NewGuid():N}");
        var foreignDirectory = Path.Combine(Path.GetTempPath(), $"mcg-foreign-{Guid.NewGuid():N}");
        using var owner = GateHeartbeatOwner.Push(scenario == "unbound" ? null : ownDirectory);
        var recordedOwner = scenario == "null" ? null : scenario == "own" ? ownDirectory : foreignDirectory;
        var baselineGates = Enumerable.Range(4701, 6).Select(pid => Gate(pid, "foreign-train-run")).ToArray();
        var baseline = Census(baselineGates);

        var census = Census(baselineGates.Select(gate => gate with { OwnerStateDirectory = recordedOwner }).ToArray());

        Assert.Equal(6, baseline.OccupiedCount);
        Assert.Equal(baseline.OccupiedCount, census.OccupiedCount);
        Assert.Equal(baseline.Describe(), census.Describe());
    }

    [Xunit.Fact]
    public void Census_ForeignFocusedEvidence_RemainsExcluded()
    {
        using var owner = GateHeartbeatOwner.Push(Path.Combine(Path.GetTempPath(), "mcg-own"));
        var gate = Gate(4801, "focused-run") with
        {
            RunClass = GateHeartbeatRunClass.FocusedEvidence,
            OwnerStateDirectory = Path.Combine(Path.GetTempPath(), "mcg-foreign")
        };

        Assert.Equal(0, Census([gate]).OccupiedCount);
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData("   ")]
    [Xunit.InlineData("bad\0owner")]
    public void Census_UnreadableProjectOwner_KeepsCounting(string recordedOwner)
    {
        using var owner = GateHeartbeatOwner.Push(Path.Combine(Path.GetTempPath(), "mcg-own"));
        var census = Census([Gate(4802, "unknown-run") with { OwnerStateDirectory = recordedOwner }]);

        Assert.Equal(1, census.OccupiedCount);
        Assert.Equal("live acceptance occupants pid:4802", census.Describe());
    }

    private static GateReadyCandidateProjection Projection(GoalId id) => new(
        id, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied, ChangeRiskTier.DocsOnly,
        ConductorTransitionDecision.Auto, [$"src/{id.Value}.cs"], [$"production:{id.Value}"],
        new GateReadyMergeEvidence(new string('d', 40), new string('c', 40),
            GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));

    private static ConductorParallelAcceptanceAttempt Attempt() => new(
        "attempt-1", "abcdef12-solo-goal", "abcdef12", 0, "branch", "main",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 4200,
        ConductorParallelAcceptanceAttemptOutcome.Running,
        "stdout", "stderr", "exit", "heartbeat", "result", "metadata");

    private static GateLoadContextProbe.LiveGateOccupant Gate(int pid, string? runId, string? goalId = null) =>
        new(pid, goalId, 0, TimeSpan.Zero, RunClass: GateHeartbeatRunClass.Acceptance, RunId: runId);

    private static ConductorBatchLoop.LiveAcceptanceCensus Census(
        IReadOnlyList<GateLoadContextProbe.LiveGateOccupant> gates,
        IReadOnlyList<ConductorAcceptanceCapacityRoot>? roots = null) =>
        ConductorBatchLoop.BuildLiveAcceptanceCensus([], new HashSet<string>(StringComparer.Ordinal),
            new ConductorAcceptanceCapacitySnapshot(roots ?? []), gates);
}
