using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its files, clock and process probes; no children are launched.
public sealed class GroupedGateAttemptLegacyRestoreTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    private const string MainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BranchRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherMainRevision = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void StartupRestoresOnlyLiveAttemptFromOverlappingFinishedHistory()
    {
        using var fixture = new Fixture();
        var completed = fixture.Attempt("cohort", ["a", "b"], leased: false);
        var overlapping = fixture.Attempt("train", ["b", "c"], leased: false);
        var failed = fixture.Attempt("cohort", ["c", "d"], leased: false);
        var live = fixture.Attempt("train", ["e", "f"]);
        Publish(completed, "completed");
        Publish(overlapping, "completed");
        Publish(failed, "failed");
        var history = new[] { completed, overlapping, failed };
        var originals = history.Select(attempt => File.ReadAllBytes(attempt.MetadataPath)).ToArray();
        var driver = Driver(fixture.Coordinator, fixture.Root);
        var restored = new List<IReadOnlySet<string>>();

        Assert.Null(Record.Exception(() => driver.GetActiveCohortGateMemberGoalIds((ids, _) => restored.Add(ids))));

        Assert.True(Assert.Single(restored).SetEquals(live.Members.Select(member => member.GoalId)));
        Assert.Equal(1, driver.CountLiveGroupedGateAttempts());
        Assert.Empty(driver.GroupedGateAdmissionHolds);
        for (var index = 0; index < history.Length; index++)
        {
            var attempt = history[index];
            Assert.Equal(originals[index], File.ReadAllBytes(attempt.MetadataPath));
            Assert.False(File.Exists(attempt.MetadataPath + ".lock"));
            Assert.False(File.Exists(AcceptanceHolderBinding.For(attempt).LeasePath));
            Assert.False(File.Exists(LegacyAcceptanceHolderSettlement.TerminalReceiptPath(AcceptanceHolderBinding.For(attempt))));
        }
    }

    [Theory]
    [InlineData("completed", "live")]
    [InlineData("failed", "live")]
    [InlineData("completed", "malformed")]
    [InlineData("failed", "malformed")]
    public void PublishedTerminalResultOutranksEveryLease(string status, string leaseState)
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt("train", ["a", "b"]);
        var binding = AcceptanceHolderBinding.For(attempt);
        if (leaseState == "malformed") File.WriteAllText(binding.LeasePath, "{");
        var originalLease = File.ReadAllBytes(binding.LeasePath);
        var originalMetadata = File.ReadAllBytes(attempt.MetadataPath);
        Publish(attempt, status);
        var driver = Driver(fixture.Coordinator, fixture.Root);

        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        Assert.Equal(0, driver.CountLiveGroupedGateAttempts());
        var reconciled = fixture.Coordinator.TryReconcileDead(attempt, "child-result-published");
        Assert.Equal(JsonSerializer.Serialize(attempt), JsonSerializer.Serialize(reconciled));
        Assert.Equal(originalLease, File.ReadAllBytes(binding.LeasePath));
        Assert.Equal(originalMetadata, File.ReadAllBytes(attempt.MetadataPath));
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    [InlineData("completed", true)]
    [InlineData("failed", true)]
    public void FinishedLeasedAttemptSettlesAfterReleaseOrHolderDeath(string status, bool released)
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt("train", ["a", "b"]);
        var binding = AcceptanceHolderBinding.For(attempt);
        Publish(attempt, status);
        File.WriteAllText(attempt.ResultPath, JsonSerializer.Serialize(new
            { Status = status, Verdict = "passed", ReceiptId = "published-receipt" }));
        var result = File.ReadAllBytes(attempt.ResultPath);
        var exit = File.ReadAllBytes(attempt.ExitCodePath);
        fixture.Status = SpawnTrackedProcessStatus.DeadOrRecycled;
        if (released)
        {
            Assert.False(fixture.Lease.Observe(binding).ReservesCapacity);
            Assert.Equal("Released", fixture.Lease.Read(binding).Phase);
            fixture.Status = SpawnTrackedProcessStatus.LiveMatch;
        }
        var metadata = File.ReadAllBytes(attempt.MetadataPath);
        var lease = File.ReadAllBytes(binding.LeasePath);

        Assert.False(fixture.Coordinator.ReservesCapacity(attempt));
        Assert.Equal(metadata, File.ReadAllBytes(attempt.MetadataPath));
        Assert.Equal(lease, File.ReadAllBytes(binding.LeasePath));
        var driver = Driver(fixture.Coordinator, fixture.Root);
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());

        var settled = ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath);
        Assert.Equal("Reconciled", settled.Outcome);
        Assert.NotNull(settled.ReconciledAt);
        Assert.Equal("child-result-published", settled.Detail);
        Assert.Equal("Released", fixture.Lease.Read(binding).Phase);
        Assert.Equal(result, File.ReadAllBytes(attempt.ResultPath));
        Assert.Equal(exit, File.ReadAllBytes(attempt.ExitCodePath));
        var expectedEvent = status == "completed"
            ? "ACCEPTANCE_COHORT_CHILD_COMPLETED " : "ACCEPTANCE_COHORT_RECONCILED_DEAD ";
        var settlementEvent = Assert.Single(fixture.Events.Where(line =>
            line.StartsWith(expectedEvent, StringComparison.Ordinal)));
        Assert.Contains($"attempt={attempt.AttemptId}", settlementEvent);
        if (status == "completed") Assert.Contains("verdict=passed receipt=published-receipt", settlementEvent);
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        Assert.Single(fixture.Events.Where(line => line.StartsWith(expectedEvent, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public void FinishedLegacyAttemptSettlesOnlyAfterHolderDeath(string status)
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt("train", ["a", "b"], leased: false);
        Publish(attempt, status);
        var result = File.ReadAllBytes(attempt.ResultPath);
        fixture.Status = SpawnTrackedProcessStatus.DeadOrRecycled;
        var driver = Driver(fixture.Coordinator, fixture.Root);

        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());

        var settled = ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath);
        Assert.Equal("Reconciled", settled.Outcome);
        Assert.NotNull(settled.ReconciledAt);
        Assert.Equal(result, File.ReadAllBytes(attempt.ResultPath));
        Assert.False(File.Exists(AcceptanceHolderBinding.For(attempt).LeasePath));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public void FinishedLegacyRecoveryDoesNotRewriteMetadataOrCreateSidecars(string status)
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt("train", ["a", "b"], leased: false);
        Publish(attempt, status);
        var original = File.ReadAllBytes(attempt.MetadataPath);
        var files = Directory.GetFiles(Path.GetDirectoryName(attempt.MetadataPath)!).Order().ToArray();

        Assert.False(fixture.Coordinator.ReservesCapacity(attempt));
        Assert.Equal(attempt, fixture.Coordinator.TryReconcileDead(attempt, "child-result-published"));
        Assert.Equal(original, File.ReadAllBytes(attempt.MetadataPath));
        Assert.Equal(files, Directory.GetFiles(Path.GetDirectoryName(attempt.MetadataPath)!).Order().ToArray());
    }

    [Fact]
    public void ExistingUnreadableLeaseKeepsCapacityAndReportsUnavailable()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt("train", ["a", "b"]) with { AdoptedByGenerationId = null };
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        var binding = AcceptanceHolderBinding.For(attempt);
        File.WriteAllText(binding.LeasePath, "{malformed");
        Assert.True(File.Exists(binding.LeasePath));
        var driver = Driver(fixture.Coordinator, fixture.Root);

        Assert.True(driver.GetActiveCohortGateMemberGoalIds().SetEquals(["a", "b"]));
        Assert.True(fixture.Coordinator.ReservesCapacity(attempt));
        Assert.Equal(1, driver.CountLiveGroupedGateAttempts());
        Assert.Contains(fixture.Events, line => line.StartsWith("ACCEPTANCE_HOLDER_HOLD ", StringComparison.Ordinal) &&
            line.Contains("reason=holder-lease-unavailable", StringComparison.Ordinal));
        Assert.False(fixture.Lease.Observe(binding).LeaseAbsent);
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    [InlineData("completed", true)]
    [InlineData("failed", true)]
    public void FinishedFollowerDoesNotBlockItsLeader(string status, bool leased)
    {
        using var fixture = new Fixture();
        var finished = fixture.Attempt("follower", ["a", "b"], leased);
        Publish(finished, status);
        var original = File.ReadAllBytes(finished.MetadataPath);
        var driver = Driver(fixture.Coordinator, fixture.Root);
        SetField(driver, "_cohortAcceptanceVerifier", new UnexpectedVerifier());

        // A different main makes the next admission decision observable without creating a worktree.
        var outcome = driver.StartFollowerGate(Projections(["a"])[0], Projections(["c"], OtherMainRevision)[0],
            ConductorAutonomyPolicy.Conservative);

        Assert.IsType<FollowerGateStartOutcome.MainDiffers>(outcome);
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        Assert.Equal(0, driver.CountLiveGroupedGateAttempts());
        Assert.Equal(0, fixture.Launches);
        Assert.Equal(original, File.ReadAllBytes(finished.MetadataPath));
    }

    [Fact]
    public void LiveFollowerStillBlocksItsLeader()
    {
        using var fixture = new Fixture();
        fixture.Attempt("follower", ["a", "b"]);
        var driver = Driver(fixture.Coordinator, fixture.Root);
        SetField(driver, "_cohortAcceptanceVerifier", new UnexpectedVerifier());

        Assert.IsType<FollowerGateStartOutcome.LeaderHasFollower>(driver.StartFollowerGate(
            Projections(["a"])[0], Projections(["c"], OtherMainRevision)[0], ConductorAutonomyPolicy.Conservative));
        Assert.Equal(1, driver.CountLiveGroupedGateAttempts());
        Assert.Equal(0, fixture.Launches);
    }

    [Fact]
    public void MissingLeaseIsDistinctAndLeaseRequiredCallersStillHold()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt("train", ["a", "b"], leased: false);
        var observation = fixture.Lease.Observe(AcceptanceHolderBinding.For(attempt));
        Assert.True(observation.LeaseAbsent);
        Assert.True(observation.ReservesCapacity);
        Assert.Equal("holder-lease-absent", observation.Reason);
    }

    [Theory]
    [InlineData("cohort", false)]
    [InlineData("train", false)]
    [InlineData("cohort", true)]
    [InlineData("follower", false)]
    [InlineData("follower", true)]
    public void LiveOverlapHoldsAdmissionUntilPublishedSettlement(string secondKind, bool identical)
    {
        using var fixture = new Fixture();
        var firstKind = secondKind == "follower" && identical ? "follower" : "cohort";
        var first = fixture.Attempt(firstKind, ["a", "b"]);
        var second = fixture.Attempt(secondKind, identical ? ["a", "b"] : ["b", "c"]);
        var driver = Driver(fixture.Coordinator, fixture.Root);
        var restored = new List<IReadOnlySet<string>>();

        Assert.Null(Record.Exception(() => driver.GetActiveCohortGateMemberGoalIds((ids, _) => restored.Add(ids))));
        Assert.Equal(2, restored.Count);
        Assert.Equal(2, driver.CountLiveGroupedGateAttempts());
        var hold = Assert.Single(driver.GroupedGateAdmissionHolds);
        Assert.Equal("grouped-gate-attempt-overlap", hold.Reason);
        Assert.Contains("b", hold.OverlappingMembers);
        Assert.Equal(new[] { first.MetadataPath, second.MetadataPath }.Order(),
            new[] { hold.FirstRecordPath, hold.SecondRecordPath }.Order());
        var overlapEvent = Assert.Single(fixture.Events.Where(line => line.StartsWith("GROUPED_GATE_ATTEMPT_OVERLAP ", StringComparison.Ordinal)));
        Assert.Contains(first.MetadataPath, overlapEvent, StringComparison.Ordinal);
        Assert.Contains(second.MetadataPath, overlapEvent, StringComparison.Ordinal);
        Assert.Contains($"Kind={secondKind}", overlapEvent, StringComparison.Ordinal);

        driver.GetActiveCohortGateMemberGoalIds();
        Assert.Single(fixture.Events.Where(line => line.StartsWith("GROUPED_GATE_ATTEMPT_OVERLAP ", StringComparison.Ordinal)));
        StartGrouped(driver, ["x", "y"]); // disjoint admission must also be held
        SetField(driver, "_cohortAcceptanceVerifier", new UnexpectedVerifier());
        var followers = Projections(["x", "y"]);
        Assert.IsType<FollowerGateStartOutcome.FollowerBusy>(driver.StartFollowerGate(
            followers[0], followers[1], ConductorAutonomyPolicy.Conservative));
        Assert.Equal(0, fixture.Launches);
        Assert.Equal(2, fixture.Coordinator.ReadAll().Count());
        Assert.Contains(fixture.Events, line => line.StartsWith("GROUPED_GATE_ADMISSION_HOLD reason=grouped-gate-attempt-overlap", StringComparison.Ordinal));

        Publish(second, "completed");
        restored.Clear();
        driver.GetActiveCohortGateMemberGoalIds((ids, _) => restored.Add(ids));
        Assert.Single(restored);
        Assert.Empty(driver.GroupedGateAdmissionHolds);
        Assert.Equal(1, driver.CountLiveGroupedGateAttempts());

        // Clearing the derived hold also clears event deduplication for a later recurrence.
        File.Delete(second.ResultPath);
        File.Delete(second.ExitCodePath);
        driver.GetActiveCohortGateMemberGoalIds();
        Assert.Single(driver.GroupedGateAdmissionHolds);
        Assert.Equal(2, fixture.Events.Count(line => line.StartsWith("GROUPED_GATE_ATTEMPT_OVERLAP ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("live", true)]
    [InlineData("dead", false)]
    [InlineData("recycled", false)]
    [InlineData("unknown", true)]
    [InlineData("reconciled", false)]
    [InlineData("terminal-metadata", false)]
    [InlineData("exit", false)]
    [InlineData("result", false)]
    public void CompleteLegacyIdentityUsesPreLeaseRuleWithoutRewriting(string condition, bool reserves)
    {
        using var fixture = new Fixture();
        fixture.Status = condition switch
        {
            "dead" or "recycled" => SpawnTrackedProcessStatus.DeadOrRecycled,
            "unknown" => SpawnTrackedProcessStatus.Unknown,
            _ => SpawnTrackedProcessStatus.LiveMatch
        };
        var attempt = fixture.Attempt("train", ["a", "b"], leased: false) with
        {
            ReconciledAt = condition == "reconciled" ? DateTimeOffset.UnixEpoch : null,
            Outcome = condition == "terminal-metadata" ? "Passed" : "Running",
            AdoptedByGenerationId = null
        };
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        if (condition == "exit") File.WriteAllText(attempt.ExitCodePath, "0");
        if (condition == "result") File.WriteAllText(attempt.ResultPath, "{");
        var original = File.ReadAllBytes(attempt.MetadataPath);
        var files = Directory.GetFiles(Path.GetDirectoryName(attempt.MetadataPath)!).Order().ToArray();
        var driver = Driver(fixture.Coordinator, fixture.Root);

        Assert.Equal(reserves, fixture.Coordinator.ReservesCapacity(attempt));
        Assert.Equal(reserves ? 1 : 0, driver.CountLiveGroupedGateAttempts());
        Assert.Equal(reserves ? 2 : 0, driver.GetActiveCohortGateMemberGoalIds().Count);
        if (condition is "reconciled" or "terminal-metadata" or "exit" or "result")
            Assert.Equal(attempt, fixture.Coordinator.TryReconcileDead(attempt, "owner-dead"));
        Assert.Equal(original, File.ReadAllBytes(attempt.MetadataPath));
        Assert.Equal(files, Directory.GetFiles(Path.GetDirectoryName(attempt.MetadataPath)!).Order().ToArray());
    }

    [Theory]
    [InlineData("{\"Status\":\"completed\"}", false)]
    [InlineData("{", true)]
    [InlineData("{}", true)]
    [InlineData("{\"Status\":\"running\"}", true)]
    [InlineData("null", true)]
    public void IncompletePublicationFallsBackToLiveLease(string result, bool exit)
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt("train", ["a", "b"]);
        File.WriteAllText(attempt.ResultPath, result);
        if (exit) File.WriteAllText(attempt.ExitCodePath, "0");
        var driver = Driver(fixture.Coordinator, fixture.Root);
        Assert.Equal(2, driver.GetActiveCohortGateMemberGoalIds().Count);
        Assert.Equal(1, driver.CountLiveGroupedGateAttempts());
    }

    [Fact]
    public void RealHistoryRestore()
    {
        var source = Environment.GetEnvironmentVariable("MCG_TEST_INPUT_GROUPED_GATE_ATTEMPTS_COPY");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(source), "Operator supplies MCG_TEST_INPUT_GROUPED_GATE_ATTEMPTS_COPY through scripts/Invoke-TestSummary.ps1 -TestInput for a copied grouped-gate-attempts directory.");
        Assert.True(Directory.Exists(source), "Copied grouped-gate-attempts directory must exist.");
        var root = Path.Combine(Path.GetTempPath(), $"grouped-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var jobSources = new Dictionary<string, (string Name, string Settlement)>(StringComparer.Ordinal);
            var originals = Directory.GetFiles(source!, "*.attempt.json", SearchOption.AllDirectories);
            Assert.NotEmpty(originals);
            foreach (var path in originals) CopyAttempt(path, root, jobSources);
            // Real identity and job queries, with process/job cancellation explicitly disabled.
            var lease = new AcceptanceHolderLease(stopUnclaimed: _ => false,
                job: (name, holder, status, cancel) =>
                {
                    Assert.False(cancel, "History inspection must never cancel a job.");
                    var original = jobSources[name];
                    return DurableAcceptanceJob.Observe(original.Name, holder, status, false, original.Settlement);
                });
            var coordinator = new ConductorGroupedGateAttemptCoordinator(root, stop: _ => false, holderLease: lease,
                eventSink: _output.WriteLine);
            var driver = Driver(coordinator, root);
            Assert.Null(Record.Exception(() => driver.GetActiveCohortGateMemberGoalIds()));
            var reserving = coordinator.ReadAll().Where(coordinator.ReservesCapacity).ToArray();
            Assert.Equal(originals.Length, coordinator.ReadAll().Count());
            Assert.Equal(reserving.Length, driver.CountLiveGroupedGateAttempts());
            _output.WriteLine($"HISTORY_RESTORE completed=true records={originals.Length} reserving={reserving.Length} " +
                $"ids={string.Join(',', reserving.Select(attempt => attempt.AttemptId))}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CopyAttempt(string sourcePath, string root,
        Dictionary<string, (string Name, string Settlement)> jobSources)
    {
        var original = ConductorGroupedGateAttemptCoordinator.Read(sourcePath);
        var directory = Path.Combine(root, original.AttemptId);
        Directory.CreateDirectory(directory);
        string Local(string path) => Path.Combine(directory, Path.GetFileName(path));
        var copy = original with
        {
            MetadataPath = Local(original.MetadataPath), ResultPath = Local(original.ResultPath),
            ExitCodePath = Local(original.ExitCodePath), StdoutPath = Local(original.StdoutPath),
            StderrPath = Local(original.StderrPath), ExecutionDirectory = root, StateDirectory = root
        };
        ConductorGroupedGateAttemptCoordinator.Save(copy);
        var oldBinding = AcceptanceHolderBinding.For(original);
        var newBinding = AcceptanceHolderBinding.For(copy);
        jobSources[newBinding.JobName] = (oldBinding.JobName, newBinding.SettlementPath);
        // Read only from the supplied copy, never from absolute paths recorded in live metadata.
        foreach (var recordedPath in new[] { original.ResultPath, original.ExitCodePath, oldBinding.LeasePath,
            oldBinding.SettlementPath, LegacyAcceptanceHolderSettlement.ReceiptPath(oldBinding),
            LegacyAcceptanceHolderSettlement.TerminalReceiptPath(oldBinding) })
        {
            var source = Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileName(recordedPath));
            if (!File.Exists(source)) continue;
            var destination = Local(recordedPath);
            File.Copy(source, destination);
            if (recordedPath == original.ResultPath || recordedPath == original.ExitCodePath) continue;
            // Rebind cloned sidecars to the isolated metadata. Preserve malformed input verbatim.
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(destination));
                if (node is not JsonObject document) continue;
                if (document["Binding"] is JsonObject binding &&
                    binding["MetadataPath"]?.GetValue<string>() == original.MetadataPath)
                    binding["MetadataPath"] = copy.MetadataPath;
                if (recordedPath == oldBinding.LeasePath && document["JobName"]?.GetValue<string>() == oldBinding.JobName)
                    document["JobName"] = newBinding.JobName;
                File.WriteAllText(destination, document.ToJsonString());
            }
            catch (JsonException) { }
        }
    }

    private static ConductorDriver Driver(ConductorGroupedGateAttemptCoordinator coordinator, string root)
    {
        var driver = MakeDriver();
        SetField(driver, "_groupedGateAttempts", coordinator);
        SetField(driver, "_executionDirectory", root);
        return driver;
    }

    private static void SetField(ConductorDriver driver, string name, object value) =>
        typeof(ConductorDriver).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(driver, value);

    private static void StartGrouped(ConductorDriver driver, string[] members) =>
        typeof(ConductorDriver).GetMethod("StartGroupedGateAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(driver, ["cohort", Projections(members), MainRevision, "tree", "manifest", "new-identity", "new-key",
                ConductorAutonomyPolicy.Conservative]);

    private static GateReadyCandidateProjection[] Projections(string[] ids, string mainRevision = MainRevision) => ids.Select(id =>
        new ConductorGroupedGateMember(id, BranchRevision, BranchRevision, ["src/" + id], [], ChangeRiskTier.Behavior)
            .ToProjection(mainRevision)).ToArray();

    private static void Publish(ConductorGroupedGateAttempt attempt, string status)
    {
        File.WriteAllText(attempt.ResultPath, JsonSerializer.Serialize(new { Status = status }));
        File.WriteAllText(attempt.ExitCodePath, status == "failed" ? "1" : "0");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"grouped-legacy-{Guid.NewGuid():N}");
        internal List<string> Events { get; } = [];
        internal SpawnTrackedProcessStatus Status { get; set; } = SpawnTrackedProcessStatus.LiveMatch;
        internal int Launches { get; private set; }
        internal AcceptanceHolderLease Lease { get; }
        internal ConductorGroupedGateAttemptCoordinator Coordinator { get; }
        private readonly SpawnProcessIdentity _generation = new(111, DateTimeOffset.UnixEpoch, @"C:\dotnet.exe");
        private readonly SpawnProcessIdentity _holder = new(222, DateTimeOffset.UnixEpoch, @"C:\dotnet.exe");

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Lease = new AcceptanceHolderLease(utcNow: () => DateTimeOffset.UnixEpoch,
                classify: _ => Status, attach: _ => new EmptyContainment(),
                job: (_, _, status, cancel) =>
                {
                    Assert.False(cancel);
                    return new(status == SpawnTrackedProcessStatus.LiveMatch,
                        status == SpawnTrackedProcessStatus.DeadOrRecycled, "fixture-holder");
                });
            Coordinator = new ConductorGroupedGateAttemptCoordinator(Root, generationId: 111,
                launch: _ => { Launches++; throw new InvalidOperationException("Unexpected launch."); },
                classifyHolder: _ => Status, holderLease: Lease, generationIdentity: _generation,
                utcNow: () => DateTimeOffset.UnixEpoch, stop: _ => false, eventSink: Events.Add);
        }

        internal ConductorGroupedGateAttempt Attempt(string kind, string[] members, bool leased = true)
        {
            var attempt = Coordinator.Create(kind, Projections(members), MainRevision, "tree", "manifest",
                Guid.NewGuid().ToString("N"), Root, ConductorAutonomyPolicy.Conservative) with
            {
                OwnerProcessId = _holder.ProcessId, OwnerProcessStartedAt = _holder.StartedAt,
                OwnerExecutablePath = _holder.ImagePath, AdoptedByGenerationId = Coordinator.GenerationId,
                HolderContractVersion = leased ? 1 : 0
            };
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            if (leased)
            {
                var binding = AcceptanceHolderBinding.For(attempt);
                Lease.Reserve(binding, _generation);
                using var containment = Lease.Claim(binding, _holder);
            }
            return attempt;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class EmptyContainment : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class UnexpectedVerifier : IGoalAcceptanceVerifier
    {
        public string ComputeEffectivePlanIdentity(string worktreePath, IReadOnlyList<string>? changedFiles = null) =>
            throw new InvalidOperationException("Admission hold must precede plan computation.");

        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner) => throw new InvalidOperationException("Unexpected gate execution.");

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) =>
            throw new InvalidOperationException("Unexpected focused evidence.");
    }
}
