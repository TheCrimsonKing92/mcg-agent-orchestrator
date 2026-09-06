using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorEvidenceAttemptLifecycleTests
{
    [Fact]
    public async Task AttemptWriterLease_WhenHeld_TimesOutWithTypedReceipt()
    {
        var root = CreateTempDirectory();
        using var holderAcquired = new ManualResetEventSlim();
        using var holderRelease = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, StorageRetentionMaintenance.AttemptLeaseNameFor(root));
            mutex.WaitOne();
            try
            {
                holderAcquired.Set();
                Assert.True(holderRelease.Wait(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        });

        Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(10)));
        string? receipt = null;
        var started = Stopwatch.StartNew();
        try
        {
            var exception = Assert.Throws<TimeoutException>(() =>
                StorageRetentionMaintenance.AcquireAttemptWriterLease(
                    root,
                    TimeSpan.FromMilliseconds(50),
                    value => receipt = value));

            Assert.Contains("ACCEPTANCE_ARTIFACT_LEASE_TIMEOUT", exception.Message, StringComparison.Ordinal);
            Assert.Contains("timeout_ms=50", receipt, StringComparison.Ordinal);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            holderRelease.Set();
            await holder;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LiveAttemptObservation_DoesNotEnterWriterLeaseBoundary()
    {
        var root = CreateTempDirectory();
        using var holderAcquired = new ManualResetEventSlim();
        using var holderRelease = new ManualResetEventSlim();
        Thread? observationThread = null;
        Thread? holder = null;
        Exception? holderException = null;
        Exception? observationException = null;
        try
        {
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Observe a live acceptance attempt without taking its writer lease");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-live", "main-live");
            var starter = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: processId => processId == 7115,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7115),
                acquireStableSlotLease: (_, _) => null);
            var started = starter.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);

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
            });
            holder.IsBackground = true;
            holder.Start();
            Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(10)));

            var leaseBoundaryEntered = false;
            var observer = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: processId => processId == 7115,
                launchOwnedProcess: _ => throw new InvalidOperationException("live observation must not launch"),
                acquireStableSlotLease: (_, _) => null,
                attemptWriterLeaseAcquiringForTests: () => leaseBoundaryEntered = true);
            ConductorParallelAcceptanceAttemptDecision? observed = null;
            observationThread = new Thread(() =>
            {
                try
                {
                    observed = observer.EvaluateFocusedEvidence(
                        candidate,
                        ConductorAutonomyPolicy.Permissive,
                        "run focused tests",
                        PassingEvidence);
                }
                catch (Exception ex)
                {
                    observationException = ex;
                }
            });
            observationThread.IsBackground = true;
            observationThread.Start();

            Assert.True(
                observationThread.Join(TimeSpan.FromSeconds(5)),
                "Live observation blocked at the writer-lease boundary.");
            Assert.Null(observationException);
            Assert.NotNull(observed);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, observed.Kind);
            Assert.False(leaseBoundaryEntered);

            var falseNegativeLeaseBoundaryEntered = false;
            var falseNegativeObserver = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("lease-busy observation must not launch"),
                acquireStableSlotLease: (_, _) => null,
                attemptWriterLeaseAcquiringForTests: () => falseNegativeLeaseBoundaryEntered = true);
            var falseNegativeElapsed = Stopwatch.StartNew();

            var leaseBusy = Assert.Throws<AcceptanceArtifactWriterLeaseBusyException>(() =>
                falseNegativeObserver.EvaluateFocusedEvidence(
                    candidate,
                    ConductorAutonomyPolicy.Permissive,
                    "run focused tests",
                    PassingEvidence));

            Assert.Equal(started.Attempt.AttemptId, leaseBusy.ObservedAttemptId);
            Assert.True(falseNegativeLeaseBoundaryEntered);
            Assert.True(falseNegativeElapsed.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            holderRelease.Set();
            if (holder is not null)
            {
                holder.Join(TimeSpan.FromSeconds(10));
            }
            if (observationThread is not null && observationThread.IsAlive)
            {
                observationThread.Join(TimeSpan.FromSeconds(10));
            }
            Directory.Delete(root, recursive: true);
        }
        Assert.Null(holderException);
    }

    [Fact]
    public void WriterLeaseBusyBeforeFirstMetadataWriteSignalsDeferredObservation()
    {
        var root = CreateTempDirectory();
        using var holderAcquired = new ManualResetEventSlim();
        using var holderRelease = new ManualResetEventSlim();
        Exception? holderException = null;
        Thread? holder = null;
        try
        {
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Defer while first acceptance metadata is not visible");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-new", "main-new");
            var goalDirectory = Path.Combine(attemptRoot, goal.Id.Value);
            Directory.CreateDirectory(goalDirectory);
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
            });
            holder.IsBackground = true;
            holder.Start();
            Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(10)));

            var observer = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("lease-busy observation must not launch"),
                acquireStableSlotLease: (_, _) => null);
            var elapsed = Stopwatch.StartNew();

            var leaseBusy = Assert.Throws<AcceptanceArtifactWriterLeaseBusyException>(() =>
                observer.EvaluateFocusedEvidence(
                    candidate,
                    ConductorAutonomyPolicy.Permissive,
                    "run focused tests",
                    PassingEvidence));

            Assert.Null(leaseBusy.ObservedAttemptId);
            Assert.Equal(Path.GetFullPath(goalDirectory), Path.GetFullPath(leaseBusy.GoalDirectory));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            holderRelease.Set();
            holder?.Join(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }

        Assert.Null(holderException);
    }

    [Fact]
    public void OwnedProcess_LeaseTimeoutWritesTransientExitReceipt()
    {
        var root = CreateTempDirectory();
        using var holderAcquired = new ManualResetEventSlim();
        using var holderRelease = new ManualResetEventSlim();
        Exception? holderException = null;
        var holder = new Thread(() =>
        {
            try
            {
                var goalDirectory = Directory.EnumerateDirectories(root).Single();
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
        });
        holder.IsBackground = true;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Write a typed child exit receipt after writer-lease timeout");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-timeout", "main-timeout");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root);
            var attempt = coordinator.CreateAttemptForTests(candidate);
            holder.Start();
            Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(10)));

            var exitCode = ConductorParallelAcceptanceAttemptCoordinator.RunOwnedProcess(
                attempt.MetadataPath,
                TimeSpan.FromMilliseconds(50));

            Assert.Equal(1, exitCode);
            Assert.Equal("1", File.ReadAllText(attempt.ExitCodePath));
            var persisted = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(attempt.MetadataPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred, persisted?.Outcome);
        }
        finally
        {
            holderRelease.Set();
            holder.Join(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
        Assert.Null(holderException);
    }

    [Fact]
    public void PassedResult_WhenCompletionLogAppendFaults_RetainsPublishedExitReceipt()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Retain a passed exit receipt after result publication");
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                [],
                branchHeadSha: "branch-passed",
                mainHeadSha: "main-passed");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                acquireStableSlotLease: (_, _) => null);
            var attempt = coordinator.CreateAttemptForTests(candidate);
            using var ownedStdout = new FileStream(
                attempt.StdoutPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);

            coordinator.RunAttemptForTests(
                attempt,
                candidate,
                ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, _) => ConductorParallelAcceptanceRunResult.Accepted(
                    attemptCandidate,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria));

            using var result = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
            Assert.True(result.RootElement.GetProperty("acceptance").GetProperty("passed").GetBoolean());
            Assert.Equal("0", File.ReadAllText(attempt.ExitCodePath));
            var persisted = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(attempt.MetadataPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, persisted?.Outcome);
            Assert.Equal(
                GoalTerminalReconciliationEvidenceState.Present,
                GoalTerminalReconciliationEvidenceResolver.Resolve(attempt.MetadataPath).State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppendAllText_AgainstOwnedSharedWriteHandle_ThrowsSharingViolation()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "owned.stdout.log");
            using var ownedWriter = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);

            Assert.Throws<IOException>(() => File.AppendAllText(path, "second writer"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OrdinarySuccess_UsesOwnedLogWritersAndPublishesConsistentEvidence()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Publish consistent ordinary acceptance success");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-success", "main-success");
            var creator = new ConductorParallelAcceptanceAttemptCoordinator(root);
            var attempt = creator.CreateAttemptForTests(candidate);
            using var logs = new AttemptLogWriterFixture(attempt);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                acquireStableSlotLease: (_, _) => null,
                attemptLogWriters: logs.Writers);

            coordinator.RunAttemptForTests(
                attempt,
                candidate,
                ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, _) => ConductorParallelAcceptanceRunResult.Accepted(
                    attemptCandidate,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria));

            Assert.Equal("0", File.ReadAllText(attempt.ExitCodePath));
            Assert.Contains(
                $"{attempt.Kind} attempt {attempt.AttemptId} completed outcome=Passed",
                ReadAllTextShared(attempt.StdoutPath),
                StringComparison.Ordinal);
            Assert.Equal(string.Empty, ReadAllTextShared(attempt.StderrPath));
            Assert.Equal(
                GoalTerminalReconciliationEvidenceState.Present,
                GoalTerminalReconciliationEvidenceResolver.Resolve(attempt.MetadataPath).State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailureBeforeResult_PublishesFailureReceiptAndDiagnostic()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Publish distinct pre-result acceptance failure");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-failure", "main-failure");
            var creator = new ConductorParallelAcceptanceAttemptCoordinator(root);
            var attempt = creator.CreateAttemptForTests(candidate);
            using var logs = new AttemptLogWriterFixture(attempt);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                acquireStableSlotLease: (_, _) => null,
                attemptLogWriters: logs.Writers);

            coordinator.RunAttemptForTests(
                attempt,
                candidate,
                ConductorAutonomyPolicy.Permissive,
                (_, _) => throw new IOException("failure before result publication"));

            Assert.False(File.Exists(attempt.ResultPath));
            Assert.Equal("1", File.ReadAllText(attempt.ExitCodePath));
            var persisted = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(attempt.MetadataPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts, persisted?.Outcome);
            Assert.Contains("failure before result publication", ReadAllTextShared(attempt.StderrPath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailureAfterResult_RetainsPublishedVerdictAndRecordsCleanupEvidence()
    {
        var root = CreateTempDirectory();
        using var cleanupReached = new ManualResetEventSlim();
        using var allowCleanupFailure = new ManualResetEventSlim();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Retain acceptance result across cleanup failure");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-cleanup", "main-cleanup");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                cleanupObservedForTests: (_, phase) =>
                {
                    if (!string.Equals(phase, "artifact-custody-released", StringComparison.Ordinal))
                    {
                        return;
                    }

                    cleanupReached.Set();
                    Assert.True(allowCleanupFailure.Wait(TimeSpan.FromSeconds(10)));
                    throw new IOException("cleanup failed after result publication");
                },
                acquireStableSlotLease: (_, _) => CreateFakeStableSlotLease(root));
            var attempt = coordinator.CreateAttemptForTests(candidate);
            var runTask = Task.Run(() => coordinator.RunAttemptForTests(
                attempt,
                candidate,
                ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, _) => ConductorParallelAcceptanceRunResult.Accepted(
                    attemptCandidate,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria)));

            Assert.True(cleanupReached.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal("0", File.ReadAllText(attempt.ExitCodePath));
            Assert.Equal(
                GoalTerminalReconciliationEvidenceState.Present,
                GoalTerminalReconciliationEvidenceResolver.Resolve(attempt.MetadataPath).State);
            allowCleanupFailure.Set();

            var exception = await Assert.ThrowsAsync<IOException>(async () => await runTask);
            coordinator.CompleteOwnedProcessFailureForTests(attempt, exception);

            Assert.Equal("0", File.ReadAllText(attempt.ExitCodePath));
            Assert.Contains("cleanup failed after result publication", File.ReadAllText(attempt.StderrPath), StringComparison.Ordinal);
            Assert.Equal(
                GoalTerminalReconciliationEvidenceState.Present,
                GoalTerminalReconciliationEvidenceResolver.Resolve(attempt.MetadataPath).State);
        }
        finally
        {
            allowCleanupFailure.Set();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResultReaderOverlap_DoesNotChangePublishedVerdict()
    {
        var root = CreateTempDirectory();
        using var readerReady = new ManualResetEventSlim();
        using var releaseReader = new ManualResetEventSlim();
        Task? readerTask = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Read acceptance result while publication completes");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-reader", "main-reader");
            var creator = new ConductorParallelAcceptanceAttemptCoordinator(root);
            var attempt = creator.CreateAttemptForTests(candidate);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                acquireStableSlotLease: (_, _) => null,
                resultPublishedForTests: publishedAttempt =>
                {
                    readerTask = Task.Run(() =>
                    {
                        using var reader = new FileStream(
                            publishedAttempt.ResultPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        readerReady.Set();
                        Assert.True(releaseReader.Wait(TimeSpan.FromSeconds(10)));
                    });
                    Assert.True(readerReady.Wait(TimeSpan.FromSeconds(10)));
                });

            coordinator.RunAttemptForTests(
                attempt,
                candidate,
                ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, _) => ConductorParallelAcceptanceRunResult.Accepted(
                    attemptCandidate,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria));

            Assert.Equal("0", File.ReadAllText(attempt.ExitCodePath));
            Assert.Equal(
                GoalTerminalReconciliationEvidenceState.Present,
                GoalTerminalReconciliationEvidenceResolver.Resolve(attempt.MetadataPath).State);
        }
        finally
        {
            releaseReader.Set();
            readerTask?.GetAwaiter().GetResult();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NonTerminalAttemptCreationRetainsEveryAttemptArtifact()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Bound active acceptance attempt artifacts");
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                [],
                branchHeadSha: "branch-bounded",
                mainHeadSha: "main-bounded");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root);
            for (var ordinal = 1; ordinal <= 22; ordinal++)
            {
                coordinator.CreateAttemptForTests(candidate);
            }

            var goalDirectory = Path.Combine(root, goal.Id.Value);
            Assert.Equal(22, Directory.GetFiles(goalDirectory, "*.attempt.json").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MultiSelectionSingleFindingUsesRequestDispositionForAttemptAndEventLabels()
    {
        var root = CreateTempDirectory();
        try
        {
            var logPath = Path.Combine(root, "conduct-events.log");
            var writer = new ConductEventLogWriter(logPath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Label one finding with multiple selections consistently");
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                [],
                branchHeadSha: "abc1234",
                mainHeadSha: "base1234");
            var context = new ConductorFocusedEvidenceRequestContext(
                "finding-round-single",
                "batch-single",
                [
                    new FindingEvidenceRequestDisposition(
                        "one-finding",
                        "one-request",
                        "executed-standalone",
                        "single-request")
                ]);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7100),
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: writer);

            var run = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "Infrastructure.Tests:ConductorDriverTests; Infrastructure.Tests:GoalAcceptanceVerifierTests",
                PassingEvidence,
                context);

            Assert.Equal("executed-standalone", run.Attempt?.FocusedEvidenceRequestDisposition);
            var start = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_START"));
            Assert.Equal("executed-standalone", start.GetProperty("request_disposition").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SupersedingLiveAttempt_RecordsTypedEndBeforeReplacementStartExactlyOnce()
    {
        var root = CreateTempDirectory();
        try
        {
            var logPath = Path.Combine(root, "conduct-events.log");
            var writer = new ConductEventLogWriter(logPath);
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Capture focused evidence lifecycle");
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                [],
                branchHeadSha: "abc1234",
                mainHeadSha: "base1234");
            const string batchedRequest =
                "Infrastructure.Tests:ConductorDriverTests; " +
                "Infrastructure.Tests:GateReadyCandidateProjectorTests";
            const string roundFingerprint = "finding-round-1234";
            var requestContext = new ConductorFocusedEvidenceRequestContext(
                roundFingerprint,
                "evidence-batch-round-1234",
                [
                    new FindingEvidenceRequestDisposition(
                        "driver-finding",
                        "Infrastructure.Tests:ConductorDriverTests",
                        "executed-batched",
                        "compatible-same-project"),
                    new FindingEvidenceRequestDisposition(
                        "projector-finding",
                        "Infrastructure.Tests:GateReadyCandidateProjectorTests",
                        "executed-batched",
                        "compatible-same-project")
                ]);
            var firstCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7101),
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: writer);

            var first = firstCoordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                batchedRequest,
                PassingEvidence,
                requestContext);
            Assert.True(firstCoordinator.InvalidateCurrent(goal.Id.Value, "operator retry replaced attempt"));

            var replacementCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7102),
                recentHeartbeatGrace: TimeSpan.Zero,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: writer);
            var replacement = replacementCoordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                batchedRequest,
                PassingEvidence,
                requestContext);
            var finalDispositions = requestContext.RequestDispositions
                .Append(new FindingEvidenceRequestDisposition(
                    "pending-finding",
                    "Core.Tests:GoalLifecycleTests",
                    "superseded",
                    "superseded-by-actionable-red"))
                .ToArray();
            Assert.True(replacementCoordinator.RecordFocusedEvidenceRequestDispositions(
                replacement.Attempt,
                roundFingerprint,
                "finding-evidence-receipt-1",
                finalDispositions));
            using (var metadata = JsonDocument.Parse(File.ReadAllText(replacement.Attempt.MetadataPath)))
            {
                Assert.Equal(
                    roundFingerprint,
                    metadata.RootElement.GetProperty("findingRoundFingerprint").GetString());
                Assert.Equal(
                    "finding-evidence-receipt-1",
                    metadata.RootElement.GetProperty("focusedEvidenceReceiptId").GetString());
                Assert.Equal(
                    3,
                    metadata.RootElement.GetProperty("focusedEvidenceRequestDispositions").GetArrayLength());
            }
            replacementCoordinator.RunAttemptForTests(
                first.Attempt,
                candidate,
                ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, _, _, _) => ConductorParallelAcceptanceRunResult.Focused(
                    attemptCandidate,
                    PassingEvidence(attemptCandidate.Goal, batchedRequest, null, CancellationToken.None)));

            var events = ReadEvents(logPath);
            var firstStart = Assert.Single(events.Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_START" &&
                item.GetProperty("attempt").GetString() == first.Attempt.AttemptId));
            Assert.Equal(goal.Id.Value, firstStart.GetProperty("goal").GetString());
            Assert.Equal(first.Attempt.AttemptId, firstStart.GetProperty("attempt").GetString());
            Assert.Equal(1, firstStart.GetProperty("ordinal").GetInt32());
            Assert.Equal(JsonValueKind.String, firstStart.GetProperty("timestamp").ValueKind);
            Assert.Equal("abc1234", firstStart.GetProperty("candidate_sha").GetString());
            Assert.Equal("Permissive", firstStart.GetProperty("policy").GetString());
            Assert.Equal("evidence-batch-round-1234", firstStart.GetProperty("batch_id").GetString());
            Assert.Equal("executed-batched", firstStart.GetProperty("request_disposition").GetString());
            Assert.Equal(2, firstStart.GetProperty("member_requests").GetArrayLength());
            Assert.Equal(roundFingerprint, firstStart.GetProperty("finding_round_fingerprint").GetString());
            Assert.Equal(2, firstStart.GetProperty("request_dispositions").GetArrayLength());
            Assert.Equal(first.Attempt.FocusedEvidenceBatchId, replacement.Attempt.FocusedEvidenceBatchId);
            var dispositionEvents = events.Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_REQUEST_DISPOSITION" &&
                item.GetProperty("attempt").GetString() == replacement.Attempt.AttemptId)
                .ToArray();
            Assert.Equal(3, dispositionEvents.Length);
            var superseded = Assert.Single(dispositionEvents.Where(item =>
                item.GetProperty("finding_stable_id").GetString() == "pending-finding"));
            Assert.Equal("superseded", superseded.GetProperty("request_disposition").GetString());
            Assert.Equal("finding-evidence-receipt-1", superseded.GetProperty("receipt_id").GetString());
            Assert.Equal(roundFingerprint, superseded.GetProperty("finding_round_fingerprint").GetString());
            var endIndexes = events
                .Select((item, index) => (item, index))
                .Where(pair => pair.item.GetProperty("eventKind").GetString() == "EVIDENCE_END" &&
                    pair.item.GetProperty("attempt").GetString() == first.Attempt.AttemptId)
                .ToArray();
            var replacementStartIndex = events.FindIndex(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_START" &&
                item.GetProperty("attempt").GetString() == replacement.Attempt.AttemptId);

            var end = Assert.Single(endIndexes);
            Assert.True(end.index < replacementStartIndex);
            Assert.Equal("superseded", end.item.GetProperty("outcome").GetString());
            Assert.Equal(replacement.Attempt.AttemptId, end.item.GetProperty("superseded_by").GetString());
            Assert.Equal("retry_invalidated", end.item.GetProperty("cause").GetString());
            Assert.Equal(goal.Id.Value, end.item.GetProperty("goal").GetString());
            Assert.Equal(first.Attempt.AttemptId, end.item.GetProperty("attempt").GetString());
            Assert.Equal(1, end.item.GetProperty("ordinal").GetInt32());
            Assert.Equal(JsonValueKind.Number, end.item.GetProperty("duration_s").ValueKind);
            Assert.Equal("unknown", end.item.GetProperty("tests_executed").GetString());
            Assert.Equal(2, replacement.Attempt.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.Failed), "failed")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.Faulted), "faulted")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.Cancelled), "cancelled")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.LaunchFailed), "launch_failed")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.BlockedBuildSlot), "blocked_build_slot")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.BlockedBuildLock), "blocked_build_lock")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.InfrastructureDeferred), "infrastructure_deferred")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.CorruptArtifacts), "corrupt_artifacts")]
    public void TerminalOutcome_EmitsExactTypedTaxonomyValue(
        string expectedOutcomeName,
        string expectedToken)
    {
        var root = CreateTempDirectory();
        try
        {
            var expectedOutcome = Enum.Parse<ConductorEvidenceAttemptOutcome>(expectedOutcomeName);
            var logPath = Path.Combine(root, "conduct-events.log");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal($"Record {expectedToken} evidence outcome");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                launchOwnedProcess: expectedOutcome == ConductorEvidenceAttemptOutcome.LaunchFailed
                    ? _ => throw new InvalidOperationException("launch failed")
                    : _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7110),
                acquireStableSlotLease: expectedOutcome == ConductorEvidenceAttemptOutcome.CorruptArtifacts
                    ? (_, _) => throw new IOException("receipt write failed")
                    : (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(logPath));

            var decision = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            if (expectedOutcome != ConductorEvidenceAttemptOutcome.LaunchFailed)
            {
                coordinator.RunAttemptForTests(
                    decision.Attempt,
                    candidate,
                    ConductorAutonomyPolicy.Permissive,
                    (_, _, _, _) => RunResultForOutcome(expectedOutcome, candidate));
            }

            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END" &&
                item.GetProperty("attempt").GetString() == decision.Attempt.AttemptId));
            Assert.Equal(expectedToken, end.GetProperty("outcome").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, "run focused tests", "candidate_changed")]
    [InlineData(false, "run different focused tests", "focused_request_changed")]
    public void ReplacingCompletedAttempt_EmitsTypedMismatchCause(
        bool changeCandidate,
        string replacementRequest,
        string expectedCause)
    {
        var root = CreateTempDirectory();
        try
        {
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Record focused evidence replacement cause");
            var firstCandidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-1", "main-1");
            var firstCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7111),
                acquireStableSlotLease: (_, _) => null);
            var first = firstCoordinator.EvaluateFocusedEvidence(
                firstCandidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            firstCoordinator.RunAttemptForTests(
                first.Attempt,
                firstCandidate,
                ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, _, _, _) => ConductorParallelAcceptanceRunResult.Focused(
                    attemptCandidate,
                    PassingEvidence(attemptCandidate.Goal, "run focused tests", null, CancellationToken.None)));

            var replacementCandidate = changeCandidate
                ? ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-2", "main-1")
                : firstCandidate;
            var logPath = Path.Combine(root, "conduct-events.log");
            var replacementCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7112),
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(logPath));
            var replacement = replacementCoordinator.EvaluateFocusedEvidence(
                replacementCandidate,
                ConductorAutonomyPolicy.Permissive,
                replacementRequest,
                PassingEvidence);

            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END" &&
                item.GetProperty("attempt").GetString() == first.Attempt.AttemptId));
            Assert.Equal("superseded", end.GetProperty("outcome").GetString());
            Assert.Equal(expectedCause, end.GetProperty("cause").GetString());
            Assert.Equal(replacement.Attempt.AttemptId, end.GetProperty("superseded_by").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SupersedingAttempt_DefersMutationWhileWriterLeaseIsHeld()
    {
        var root = CreateTempDirectory();
        try
        {
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Serialize focused evidence replacement with retention");
            var firstCandidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-1", "main-1");
            var firstCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7113),
                acquireStableSlotLease: (_, _) => null);
            var first = firstCoordinator.EvaluateFocusedEvidence(
                firstCandidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            firstCoordinator.RunAttemptForTests(
                first.Attempt,
                firstCandidate,
                ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, _, _, _) => ConductorParallelAcceptanceRunResult.Focused(
                    attemptCandidate,
                    PassingEvidence(attemptCandidate.Goal, "run focused tests", null, CancellationToken.None)));

            var goalDirectory = Path.Combine(attemptRoot, goal.Id.Value);
            var sequencePath = Path.Combine(goalDirectory, "attempt-sequence.txt");
            var sequenceBefore = File.ReadAllText(sequencePath);
            using var holderAcquired = new ManualResetEventSlim();
            using var holderRelease = new ManualResetEventSlim();
            var holder = Task.Run(() =>
            {
                using var lease = StorageRetentionMaintenance.AcquireAttemptWriterLease(goalDirectory);
                holderAcquired.Set();
                if (!holderRelease.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("Acceptance writer lease release signal was not observed.");
                }
            });
            Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(30)), "Acceptance writer lease was not acquired.");

            using var replacementAtLeaseBoundary = new ManualResetEventSlim();
            var replacementCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7114),
                acquireStableSlotLease: (_, _) => null,
                attemptWriterLeaseAcquiringForTests: replacementAtLeaseBoundary.Set);
            var replacementCandidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-2", "main-1");
            var replacementTask = Task.Run(() => replacementCoordinator.EvaluateFocusedEvidence(
                replacementCandidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence));

            string sequenceWhileLeaseHeld;
            AcceptanceArtifactWriterLeaseBusyException leaseBusy;
            try
            {
                Assert.True(
                    replacementAtLeaseBoundary.Wait(TimeSpan.FromSeconds(30)),
                    "Replacement did not reach the acceptance writer lease boundary.");
                sequenceWhileLeaseHeld = File.ReadAllText(sequencePath);
                leaseBusy = await Assert.ThrowsAsync<AcceptanceArtifactWriterLeaseBusyException>(async () =>
                    await replacementTask.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            finally
            {
                holderRelease.Set();
                await holder;
            }

            Assert.Equal(sequenceBefore, sequenceWhileLeaseHeld);
            Assert.Equal(first.Attempt.AttemptId, leaseBusy.ObservedAttemptId);

            var replacement = replacementCoordinator.EvaluateFocusedEvidence(
                replacementCandidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            Assert.NotEqual(first.Attempt.AttemptId, replacement.Attempt.AttemptId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HeldReason_RefreshesElapsedWithoutChangingHoldIdentity()
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Render focused evidence progress");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103),
                acquireStableSlotLease: (_, _) => null,
                timeProvider: clock);
            var started = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);

            var firstReason = coordinator.DescribeFocusedEvidenceHold(started.Attempt);
            clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(41));
            var secondReason = coordinator.DescribeFocusedEvidenceHold(started.Attempt);
            var stableIdentity = $"pre-review-evidence:{started.Attempt.AttemptId}";
            var firstHold = kernel.ObserveGoalHold(
                goal.Id,
                "AwaitingReview",
                firstReason,
                clock.GetUtcNow() - TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(10),
                stableIdentity);
            var secondHold = kernel.ObserveGoalHold(
                goal.Id,
                "AwaitingReview",
                secondReason,
                clock.GetUtcNow(),
                TimeSpan.FromMinutes(10),
                stableIdentity);

            Assert.Contains("attempt 1, 0m0s elapsed", firstReason, StringComparison.Ordinal);
            Assert.Contains("attempt 1, 9m41s elapsed", secondReason, StringComparison.Ordinal);
            Assert.NotEqual(firstReason, secondReason);
            Assert.Equal(firstHold.Hold.Identity, secondHold.Hold.Identity);
            Assert.False(secondHold.StateChanged);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompletedRun_RecordsNumericExecutedTestCountIncludingZero()
    {
        var root = CreateTempDirectory();
        try
        {
            var logPath = Path.Combine(root, "conduct-events.log");
            var trxPath = Path.Combine(root, "zero.trx");
            File.WriteAllText(trxPath, "<TestRun><ResultSummary><Counters total=\"0\" executed=\"0\" passed=\"0\" failed=\"0\" /></ResultSummary></TestRun>");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Count focused evidence tests");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(logPath));

            var completed = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                (_, request, _, _) => new FocusedEvidenceRunResult(
                    request,
                    Accepted: true,
                    Passed: true,
                    Summary: "1 check(s) passed; mode=focused reason=explicit-focused-mapping; receipts: zero.trx",
                    Checks:
                    [
                        new AcceptanceCheckResult(
                            "zero-test receipt",
                            Passed: true,
                            ExitCode: 0,
                            OutputTail: null,
                            TestResultPaths: [trxPath])
                    ]));

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END"));
            Assert.Equal("passed", end.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Number, end.GetProperty("tests_executed").ValueKind);
            Assert.Equal(0, end.GetProperty("tests_executed").GetInt64());
            Assert.Equal(
                "1 check(s) passed; mode=focused reason=explicit-focused-mapping; receipts: zero.trx",
                end.GetProperty("detail").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeadOwnerWithoutTerminalArtifacts_RecordsTypedUnknownOutcome()
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 7, 13, 0, 0, TimeSpan.Zero));
            var logPath = Path.Combine(root, "conduct-events.log");
            var writer = new ConductEventLogWriter(logPath);
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Record an indeterminate focused evidence outcome");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var startingCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7104),
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: writer,
                timeProvider: clock);

            var started = startingCoordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            clock.Advance(TimeSpan.FromSeconds(5));

            var recoveringCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7105),
                recentHeartbeatGrace: TimeSpan.Zero,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: writer,
                timeProvider: clock);
            var terminal = recoveringCoordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, terminal.Kind);
            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END" &&
                item.GetProperty("attempt").GetString() == started.Attempt.AttemptId));
            Assert.Equal("unknown", end.GetProperty("outcome").GetString());
            Assert.Equal("unknown", end.GetProperty("tests_executed").GetString());
            Assert.Equal(JsonValueKind.Number, end.GetProperty("duration_s").ValueKind);
            Assert.True(end.GetProperty("duration_s").GetDouble() > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompletedRun_SumsPassedAndFailedAcrossDistinctTrxReceipts()
    {
        var root = CreateTempDirectory();
        try
        {
            var logPath = Path.Combine(root, "conduct-events.log");
            var firstTrxPath = Path.Combine(root, "first.trx");
            var secondTrxPath = Path.Combine(root, "second.trx");
            File.WriteAllText(firstTrxPath, TrxCounters(passed: 2, failed: 1, notExecuted: 4));
            File.WriteAllText(secondTrxPath, TrxCounters(passed: 3, failed: 2, notExecuted: 5));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Aggregate focused evidence test receipts");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(logPath));

            var completed = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                (_, request, _, _) => new FocusedEvidenceRunResult(
                    request,
                    Accepted: true,
                    Passed: true,
                    Summary: "passed",
                    Checks:
                    [
                        new AcceptanceCheckResult(
                            "multi-receipt count",
                            Passed: true,
                            ExitCode: 0,
                            OutputTail: null,
                            TestResultPaths: [firstTrxPath, secondTrxPath, firstTrxPath])
                    ]));

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END"));
            Assert.Equal(JsonValueKind.Number, end.GetProperty("tests_executed").ValueKind);
            Assert.Equal(8, end.GetProperty("tests_executed").GetInt64());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FocusedEvidenceRunResult PassingEvidence(
        Goal _,
        string request,
        DotnetBuildEnvironmentLease? __,
        CancellationToken ___) =>
        new(request, Accepted: true, Passed: true, Summary: "passed", Checks: []);

    private static ConductorParallelAcceptanceRunResult RunResultForOutcome(
        ConductorEvidenceAttemptOutcome outcome,
        ConductorParallelAcceptanceCandidate candidate) =>
        outcome switch
        {
            ConductorEvidenceAttemptOutcome.Failed => ConductorParallelAcceptanceRunResult.Focused(
                candidate,
                new FocusedEvidenceRunResult("run focused tests", Accepted: true, Passed: false, "failed", [])),
            ConductorEvidenceAttemptOutcome.Faulted => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new InvalidOperationException("evidence faulted")),
            ConductorEvidenceAttemptOutcome.Cancelled => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new OperationCanceledException("evidence cancelled")),
            ConductorEvidenceAttemptOutcome.BlockedBuildSlot => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy("evidence", []))),
            ConductorEvidenceAttemptOutcome.BlockedBuildLock => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new BuildLockBlockedException(new BuildLockAttribution("locked.dll", [], "test"))),
            ConductorEvidenceAttemptOutcome.InfrastructureDeferred => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new AcceptanceInfrastructureDeferredException(
                    "trusted-main-build-failed",
                    1,
                    "baseline assembly unavailable")),
            ConductorEvidenceAttemptOutcome.CorruptArtifacts => ConductorParallelAcceptanceRunResult.Focused(
                candidate,
                new FocusedEvidenceRunResult("run focused tests", Accepted: true, Passed: true, "unused", [])),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Outcome requires a dedicated lifecycle test.")
        };

    private static List<JsonElement> ReadEvents(string path) =>
        File.ReadLines(path)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    private static DotnetBuildEnvironmentLease CreateFakeStableSlotLease(string root)
    {
        var leaseRoot = Path.Combine(root, ".fake-build-slot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(leaseRoot);
        var lockPath = Path.Combine(leaseRoot, "build.lock");
        var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var environment = new DotnetBuildEnvironment(
            "fake-acceptance-lifecycle-slot",
            leaseRoot,
            Path.Combine(leaseRoot, "artifacts"),
            lockPath,
            [],
            "build-0",
            BuildPermitIndex: 0);
        return new DotnetBuildEnvironmentLease(environment, stream);
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string TrxCounters(int passed, int failed, int notExecuted) =>
        $"<TestRun><ResultSummary><Counters total=\"{passed + failed + notExecuted}\" " +
        $"executed=\"{passed + failed}\" passed=\"{passed}\" failed=\"{failed}\" " +
        $"notExecuted=\"{notExecuted}\" /></ResultSummary></TestRun>";

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-evidence-lifecycle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class AttemptLogWriterFixture : IDisposable
    {
        private readonly StreamWriter _stdout;
        private readonly StreamWriter _stderr;

        internal AttemptLogWriterFixture(ConductorParallelAcceptanceAttempt attempt)
        {
            _stdout = Open(attempt.StdoutPath);
            _stderr = Open(attempt.StderrPath);
            Writers = new Dictionary<string, TextWriter>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(attempt.StdoutPath)] = _stdout,
                [Path.GetFullPath(attempt.StderrPath)] = _stderr
            };
        }

        internal IReadOnlyDictionary<string, TextWriter> Writers { get; }

        public void Dispose()
        {
            _stderr.Dispose();
            _stdout.Dispose();
        }

        private static StreamWriter Open(string path) =>
            new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = utcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            _utcNow += elapsed;
        }
    }
}
