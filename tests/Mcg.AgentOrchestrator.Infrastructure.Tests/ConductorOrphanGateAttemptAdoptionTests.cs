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

    private static readonly JsonSerializerOptions AttemptJson = new(JsonSerializerDefaults.Web);

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

        Xunit.Assert.False(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
            attempt,
            SuccessorGeneration,
            _ => false,
            _ => false));
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_does_not_adopt_its_own_live_generation")]
    public void DoesNotAdoptItsOwnLiveGeneration()
    {
        var attempt = SyntheticAttempt(SuccessorGeneration);

        Xunit.Assert.False(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
            attempt,
            SuccessorGeneration,
            _ => true,
            _ => false));
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_does_not_adopt_a_foreign_generation_that_is_still_alive")]
    public void DoesNotAdoptAForeignGenerationThatIsStillAlive()
    {
        var attempt = SyntheticAttempt(FirstGeneration);

        Xunit.Assert.False(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
            attempt,
            SuccessorGeneration,
            _ => true,
            _ => false));
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_adopts_an_attempt_with_no_recorded_generation")]
    public void AdoptsAnAttemptWithNoRecordedGeneration()
    {
        var attempt = SyntheticAttempt(null);

        Xunit.Assert.True(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
            attempt,
            SuccessorGeneration,
            pid => pid == GateChildPid,
            _ => false));
    }

    [Xunit.Fact(DisplayName = "OrphanGateAttemptAdoption_does_not_adopt_once_artifacts_exist")]
    public void DoesNotAdoptOnceArtifactsExist()
    {
        var attempt = SyntheticAttempt(FirstGeneration);

        Xunit.Assert.False(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
            attempt,
            SuccessorGeneration,
            pid => pid == GateChildPid,
            path => path == attempt.ResultPath));
        Xunit.Assert.False(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
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
        Xunit.Assert.True(ConductorOrphanGateAttemptAdoption.ShouldAdopt(
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
