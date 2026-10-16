using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorOrphanGateAttemptAdoptionTests
{
    private const int FirstGeneration = 4242;
    private const int SuccessorGeneration = 909;
    private const int GateChildPid = 7115;

    private static bool ShouldAdoptFixture(
        ConductorParallelAcceptanceAttempt attempt, int generation,
        Func<int, bool> alive, Func<string, bool> artifactExists) =>
        ConductorOrphanGateAttemptAdoption.ShouldAdopt(attempt, generation, alive, artifactExists,
            current => alive(current.OwnerProcessId)
                ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled);

    [Xunit.Fact]
    public void RecycledHolderIsNotAdoptedEvenWhenPidIsAlive()
    {
        var attempt = SyntheticAttempt(FirstGeneration);
        Xunit.Assert.False(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
            attempt, SuccessorGeneration, pid => pid == GateChildPid, _ => false,
            _ => SpawnTrackedProcessStatus.DeadOrRecycled));
    }

    [Xunit.Fact]
    public void LegacyHolderWithoutRecordedIdentityIsNotAdopted()
    {
        Xunit.Assert.False(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
            SyntheticAttempt(FirstGeneration), SuccessorGeneration, pid => pid == GateChildPid, _ => false));
    }

    [Xunit.Fact]
    public void RecordedStartTimeMismatchIsNotAdopted()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var attempt = SyntheticAttempt(FirstGeneration) with
        {
            OwnerProcessId = process.Id,
            OwnerProcessStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime()).AddMinutes(-1),
            OwnerExecutablePath = process.MainModule!.FileName
        };
        Xunit.Assert.False(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
            attempt, SuccessorGeneration, pid => pid == process.Id, _ => false));
    }

    [Xunit.Fact]
    public void ImageMismatchRemainsUnknownRatherThanDead()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
        var image = process.MainModule!.FileName + ".different-image";
        Xunit.Assert.Equal(SpawnTrackedProcessStatus.Unknown,
            AcceptanceHolderIdentity.Classify(process.Id, startedAt, image));
    }

    private static readonly JsonSerializerOptions AttemptJson = new(JsonSerializerDefaults.Web);

    [Xunit.Fact]
    public void CapacityQueries_FilterByGoalWhileGlobalAdmissionIncludesForeignGoals()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var occupiedGoal = kernel.CreateGoal("Occupied goal");
            var unrelatedGoal = kernel.CreateGoal("Unrelated goal");
            var coordinator = NewCoordinator(root, FirstGeneration, _ => true,
                () => DateTimeOffset.UnixEpoch,
                _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(GateChildPid));
            var candidate = ConductorParallelAcceptanceCandidate.Create(occupiedGoal, 0, [], "branch", "main");
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);
            Xunit.Assert.Empty(coordinator.GetCapacityReservingAttempts([unrelatedGoal.Id.Value]));
            Xunit.Assert.Equal(started.Attempt.AttemptId,
                Xunit.Assert.Single(coordinator.GetCapacityReservingAttempts([occupiedGoal.Id.Value])).AttemptId);
            Xunit.Assert.Equal(started.Attempt.AttemptId,
                Xunit.Assert.Single(coordinator.GetAllCapacityReservingAttempts()).AttemptId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Running")]
    [Xunit.InlineData("Passed")]
    [Xunit.InlineData("LaunchFailed")]
    public void LegacySettlement_ReleasesSlotAfterTerminalPublicationAndAllowsRetention(
        string outcomeName)
    {
        var outcome = Enum.Parse<ConductorParallelAcceptanceAttemptOutcome>(outcomeName);
        var root = CreateTempDirectory();
        try
        {
            var legacy = SyntheticAttempt(FirstGeneration);
            var directory = Path.Combine(root, legacy.GoalId);
            Directory.CreateDirectory(directory);
            // Incomplete pre-upgrade identities need whole-tree settlement; complete dead
            // identities are released automatically by the successor admission path.
            legacy = legacy with { MetadataPath = Path.Combine(directory, legacy.AttemptId + ".attempt.json"), Outcome = outcome,
                OwnerProcessId = outcomeName == "LaunchFailed" ? 0 : GateChildPid,
                OwnerProcessStartedAt = null,
                OwnerExecutablePath = null };
            File.WriteAllText(legacy.MetadataPath, JsonSerializer.Serialize(legacy, AttemptJson));
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default,
                utcNow: () => DateTimeOffset.UnixEpoch.AddDays(1),
                classifyHolder: current => current.OwnerProcessId == 0
                    ? SpawnTrackedProcessStatus.Unknown : SpawnTrackedProcessStatus.DeadOrRecycled,
                holderLease: new AcceptanceHolderLease());
            Xunit.Assert.Single(coordinator.GetAllCapacityReservingAttempts());
            coordinator.PrepareHolderDetach(); // missing settlement retains capacity without breaking stop
            Xunit.Assert.Single(coordinator.GetAllCapacityReservingAttempts());
            var binding = AcceptanceHolderBinding.For(legacy);
            File.WriteAllText(LegacyAcceptanceHolderSettlement.ReceiptPath(binding), JsonSerializer.Serialize(
                new LegacyAcceptanceHolderSettlementReceipt(binding, legacy.OwnerProcessId,
                    legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath, legacy.StartedAt,
                    legacy.StartedAt.AddDays(1), "operator proved whole-tree exit")));
            Xunit.Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
            var terminal = ReadAttempt(legacy.MetadataPath);
            Xunit.Assert.Equal(outcome == ConductorParallelAcceptanceAttemptOutcome.Running
                ? ConductorParallelAcceptanceAttemptOutcome.ProcessDied : outcome, terminal.Outcome);
            using var metadata = JsonDocument.Parse(File.ReadAllText(legacy.MetadataPath));
            Xunit.Assert.False(StorageRetentionMaintenance.AcceptanceHolderPreventsRetention(metadata.RootElement));
            coordinator.PrepareHolderDetach();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void PublishedAndReconciledResultDoesNotReleaseLiveHolderSlot()
    {
        var root = CreateTempDirectory();
        try
        {
            var goal = new AgentOrchestratorKernel().CreateGoal("Keep holder occupancy separate from verdict");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
            var holderAlive = true;
            var coordinator = NewCoordinator(root, FirstGeneration,
                pid => pid == GateChildPid && holderAlive, () => DateTimeOffset.UnixEpoch,
                _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(GateChildPid));
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);
            PublishPassingResult(started.Attempt, candidate);
            coordinator.MarkReconciled(started.Attempt);

            var reserved = Xunit.Assert.Single(coordinator.GetCapacityReservingAttempts([goal.Id.Value]));
            Xunit.Assert.Equal(started.Attempt.AttemptId, reserved.AttemptId);
            Xunit.Assert.Equal(0, reserved.SlotIndex);

            holderAlive = false; // explicit settlement signal, independent of result publication
            Xunit.Assert.Empty(coordinator.GetCapacityReservingAttempts([goal.Id.Value]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void UnreadableIdentityRetainsSlotWithoutAdoptionOrReplacement()
    {
        var root = CreateTempDirectory();
        try
        {
            var goal = new AgentOrchestratorKernel().CreateGoal("Retain unknown holder identity");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
            var started = NewCoordinator(root, FirstGeneration, _ => true,
                () => DateTimeOffset.UnixEpoch,
                _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(GateChildPid))
                .Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);
            var launches = 0;
            var successor = new ConductorParallelAcceptanceAttemptCoordinator(
                root, TrunkBranchName.Default, utcNow: () => DateTimeOffset.UnixEpoch.AddHours(1),
                isProcessAlive: _ => false, conductorGenerationId: SuccessorGeneration,
                classifyHolder: _ => SpawnTrackedProcessStatus.Unknown,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    throw new InvalidOperationException("An unknown holder must block replacement.");
                });

            var decision = successor.Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);
            Xunit.Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, decision.Kind);
            Xunit.Assert.Equal(started.Attempt.AttemptId, decision.Attempt.AttemptId);
            Xunit.Assert.Null(decision.Attempt.AdoptedByGenerationId);
            Xunit.Assert.Equal(0, launches);
            Xunit.Assert.Single(successor.GetCapacityReservingAttempts([goal.Id.Value]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_successor_adopts_foreign_generation_and_starts_no_second_attempt")]
    public void SuccessorAdoptsForeignGenerationAndStartsNoSecondAttempt()
    {
        var root = CreateTempDirectory();
        try
        {
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Adopt a gate attempt orphaned by a conductor renewal");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-orphan", "main-orphan");
            var now = DateTimeOffset.Parse("2026-09-05T04:05:00Z");

            // Generation A launches the gate attempt; its child takes the pid.
            var started = NewCoordinator(
                attemptRoot,
                FirstGeneration,
                pid => pid is GateChildPid or FirstGeneration,
                () => now,
                _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(GateChildPid))
                .Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);
            Xunit.Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Xunit.Assert.Equal(FirstGeneration, started.Attempt.ConductorGenerationId);
            Xunit.Assert.Null(started.Attempt.AdoptedByGenerationId);

            // Generation A dies. The gate child lives on, but nothing refreshes the heartbeat, so the
            // attempt reads stale well past the grace by the time the successor's first tick runs.
            now = now.AddMinutes(30);
            var launches = 0;
            var successor = NewCoordinator(
                attemptRoot,
                SuccessorGeneration,
                pid => pid == GateChildPid,
                () => now,
                _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(GateChildPid);
                });

            var adopted = successor.Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);

            Xunit.Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, adopted.Kind);
            Xunit.Assert.Equal(0, launches);
            Xunit.Assert.Equal(started.Attempt.AttemptId, adopted.Attempt.AttemptId);
            Xunit.Assert.Equal(FirstGeneration, adopted.Attempt.ConductorGenerationId);
            Xunit.Assert.Equal(SuccessorGeneration, adopted.Attempt.AdoptedByGenerationId);
            Xunit.Assert.Equal(
                SuccessorGeneration,
                ReadAttempt(adopted.Attempt.MetadataPath).AdoptedByGenerationId);
            Xunit.Assert.Equal(now, ReadHeartbeatObservedAt(adopted.Attempt.HeartbeatPath));

            // The adoption holds across a second tick beyond the grace, and still launches nothing.
            now = now.AddMinutes(30);
            var stillAdopted = successor.Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);
            Xunit.Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, stillAdopted.Kind);
            Xunit.Assert.Equal(0, launches);
            Xunit.Assert.Equal(now, ReadHeartbeatObservedAt(stillAdopted.Attempt.HeartbeatPath));

            // When the adopted child finishes, its verdict is applied from the artifacts it published.
            PublishPassingResult(adopted.Attempt, candidate);
            var completed = successor.Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);

            Xunit.Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            Xunit.Assert.Equal(started.Attempt.AttemptId, completed.Attempt.AttemptId);
            Xunit.Assert.True(completed.Run!.Acceptance!.Passed);
            Xunit.Assert.Equal(0, launches);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_fences_a_second_attempt_even_when_the_record_cannot_be_written")]
    public void FencesASecondAttemptEvenWhenTheRecordCannotBeWritten()
    {
        var root = CreateTempDirectory();
        using var holderAcquired = new ManualResetEventSlim();
        using var holderRelease = new ManualResetEventSlim();
        Thread? holder = null;
        Exception? holderException = null;
        try
        {
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Fence a second attempt while the gate child holds the writer lease");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-held", "main-held");
            var now = DateTimeOffset.Parse("2026-09-05T04:05:00Z");
            var started = NewCoordinator(
                attemptRoot,
                FirstGeneration,
                pid => pid is GateChildPid or FirstGeneration,
                () => now,
                _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(GateChildPid))
                .Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);
            Xunit.Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);

            // A live gate child holds the attempt writer lease for its whole run, exactly as an orphan's
            // child does, so the adoption record cannot be written on this tick.
            var goalDirectory = Path.Combine(attemptRoot, goal.Id.Value);
            holder = new Thread(() =>
            {
                try
                {
                    using var lease = StorageRetentionMaintenance.AcquireAttemptWriterLease(goalDirectory);
                    holderAcquired.Set();
                    if (!holderRelease.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Writer-lease holder release signal was not observed.");
                    }
                }
                catch (Exception ex)
                {
                    holderException = ex;
                }
            })
            {
                IsBackground = true
            };
            holder.Start();
            Xunit.Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(10)));

            now = now.AddMinutes(30);
            var launches = 0;
            var adopted = NewCoordinator(
                attemptRoot,
                SuccessorGeneration,
                pid => pid == GateChildPid,
                () => now,
                _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(GateChildPid);
                })
                .Evaluate(candidate, ConductorAutonomyPolicy.Permissive, FailIfRun);

            // The fence holds and the heartbeat is adopted; only the supplementary record is deferred.
            Xunit.Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, adopted.Kind);
            Xunit.Assert.Equal(0, launches);
            Xunit.Assert.Equal(started.Attempt.AttemptId, adopted.Attempt.AttemptId);
            Xunit.Assert.Equal(now, ReadHeartbeatObservedAt(adopted.Attempt.HeartbeatPath));
            Xunit.Assert.Null(ReadAttempt(adopted.Attempt.MetadataPath).AdoptedByGenerationId);
        }
        finally
        {
            holderRelease.Set();
            holder?.Join(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }

        Xunit.Assert.Null(holderException);
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_does_not_adopt_when_the_gate_child_is_dead")]
    public void DoesNotAdoptWhenTheGateChildIsDead()
    {
        var attempt = SyntheticAttempt(FirstGeneration);

        Xunit.Assert.False(ShouldAdoptFixture(
            attempt,
            SuccessorGeneration,
            _ => false,
            _ => false));
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_does_not_adopt_its_own_live_generation")]
    public void DoesNotAdoptItsOwnLiveGeneration()
    {
        var attempt = SyntheticAttempt(SuccessorGeneration);

        Xunit.Assert.False(ShouldAdoptFixture(
            attempt,
            SuccessorGeneration,
            _ => true,
            _ => false));
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_does_not_adopt_a_foreign_generation_that_is_still_alive")]
    public void DoesNotAdoptAForeignGenerationThatIsStillAlive()
    {
        var attempt = SyntheticAttempt(FirstGeneration);

        Xunit.Assert.False(ShouldAdoptFixture(
            attempt,
            SuccessorGeneration,
            _ => true,
            _ => false));
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_adopts_an_attempt_with_no_recorded_generation")]
    public void AdoptsAnAttemptWithNoRecordedGeneration()
    {
        var attempt = SyntheticAttempt(null);

        Xunit.Assert.True(ShouldAdoptFixture(
            attempt,
            SuccessorGeneration,
            pid => pid == GateChildPid,
            _ => false));
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_does_not_adopt_once_artifacts_exist")]
    public void DoesNotAdoptOnceArtifactsExist()
    {
        var attempt = SyntheticAttempt(FirstGeneration);

        Xunit.Assert.False(ShouldAdoptFixture(
            attempt,
            SuccessorGeneration,
            pid => pid == GateChildPid,
            path => path == attempt.ResultPath));
        Xunit.Assert.False(ShouldAdoptFixture(
            attempt,
            SuccessorGeneration,
            pid => pid == GateChildPid,
            path => path == attempt.ExitCodePath));
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_records_the_adopting_generation_once")]
    public void RecordsTheAdoptingGenerationOnce()
    {
        var attempt = SyntheticAttempt(FirstGeneration);

        Xunit.Assert.True(ConductorOrphanGateAttemptAdoption.NeedsAdoptionRecord(attempt, SuccessorGeneration));
        var adopted = attempt with { AdoptedByGenerationId = SuccessorGeneration };
        Xunit.Assert.False(ConductorOrphanGateAttemptAdoption.NeedsAdoptionRecord(adopted, SuccessorGeneration));
        // Adopting does not make the attempt mine: the fence that keeps Launch unreachable must survive.
        Xunit.Assert.True(ShouldAdoptFixture(
            adopted,
            SuccessorGeneration,
            pid => pid == GateChildPid,
            _ => false));
    }

    private static ConductorParallelAcceptanceAttemptCoordinator NewCoordinator(
        string attemptRoot,
        int generationId,
        Func<int, bool> isProcessAlive,
        Func<DateTimeOffset> utcNow,
        Func<ConductorParallelAcceptanceOwnedProcessLaunch, ConductorParallelAcceptanceOwnedProcessLaunchResult> launch) =>
        new(
            attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            utcNow: utcNow,
            isProcessAlive: isProcessAlive,
            launchOwnedProcess: launch,
            acquireStableSlotLease: (_, _) => null,
            conductorGenerationId: generationId);

    private static ConductorParallelAcceptanceRunResult FailIfRun(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        DotnetBuildEnvironmentLease? lease,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions executionOptions) =>
        throw new InvalidOperationException("An adopted orphan attempt must not be re-run by the successor.");

    private static ConductorParallelAcceptanceAttempt SyntheticAttempt(int? generationId)
    {
        var prefix = Path.Combine(Path.GetTempPath(), $"mcg-synthetic-attempt-{Guid.NewGuid():N}");
        return new ConductorParallelAcceptanceAttempt(
            "attempt-1",
            GoalId.New().Value,
            "goalpref",
            0,
            "branch-orphan",
            "main-orphan",
            DateTimeOffset.Parse("2026-09-05T04:05:00Z"),
            DateTimeOffset.Parse("2026-09-05T04:05:00Z"),
            GateChildPid,
            ConductorParallelAcceptanceAttemptOutcome.Running,
            prefix + ".out.log",
            prefix + ".err.log",
            prefix + ".exit.txt",
            prefix + ".heartbeat.json",
            prefix + ".result.json",
            prefix + ".attempt.json",
            ConductorGenerationId: generationId);
    }

    private static void PublishPassingResult(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate)
    {
        var artifact = new ConductorParallelAcceptanceRunArtifact(
            "accepted",
            null,
            null,
            null,
            null,
            new AcceptanceVerificationSummary(true, [], BranchHeadSha: candidate.BranchHeadSha),
            candidate.BranchHeadSha,
            candidate.MainHeadSha,
            null,
            null);
        File.WriteAllText(attempt.ResultPath, JsonSerializer.Serialize(artifact, AttemptJson));
        File.WriteAllText(attempt.ExitCodePath, "0");
    }

    private static ConductorParallelAcceptanceAttempt ReadAttempt(string metadataPath) =>
        JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(metadataPath),
            AttemptJson)!;

    private static DateTimeOffset ReadHeartbeatObservedAt(string heartbeatPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(heartbeatPath));
        return DateTimeOffset.Parse(document.RootElement.GetProperty("lastObservedAt").GetString()!);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-orphan-adoption-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
