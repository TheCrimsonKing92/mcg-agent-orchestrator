using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class DotnetBuildEnvironmentManagerStableSlotHolderLabelTests : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Fact]
    public void CohortAndGoalAttemptWriteLabelsAndBusyLineNamesBoth()
    {
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () =>
            new ProcessCommandLineSnapshot(new Dictionary<int, string>());
        try
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(StorageRoot.RootPath, "attempts"), runInline: true, buildStorageRoot: StorageRoot);
            using var cohort = coordinator.AcquireCohortStableSlotLease("identity", timeout: TimeSpan.Zero,
                holderLabel: "cohort-gate:goal-aaaaaaaa+bbbbbbbb");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Gate member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Member.cs"], "branch", "main");
            string? attemptLabel = null;
            string? metadataLabel = null;
            string? busyOutput = null;
            DotnetBuildLeaseAcquisition.SlotsBusy? busy = null;
            var callbacks = 0;

            var decision = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
                (current, _, lease, _, _) =>
                {
                    callbacks++;
                    if (lease is not null)
                    {
                        attemptLabel = lease.Environment.HolderLabel;
                        metadataLabel = ReadLabel(lease.Environment);
                        busyOutput = AsyncLocalConsoleRouter.Capture(() => busy =
                            DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
                                TimeSpan.Zero, storageRoot: StorageRoot) as DotnetBuildLeaseAcquisition.SlotsBusy);
                    }
                    return ConductorParallelAcceptanceRunResult.Accepted(current,
                        AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);
                });

            var expectedAttemptLabel = $"parallel-acceptance:goal-{goal.Id.Value[..8]}:attempt-{decision.Attempt.AttemptId}";
            Assert.Equal(1, callbacks);
            Assert.Equal(expectedAttemptLabel, attemptLabel);
            Assert.Equal(expectedAttemptLabel, metadataLabel);
            Assert.Equal("cohort-gate:goal-aaaaaaaa+bbbbbbbb", ReadLabel(cohort.Environment));
            Assert.NotNull(busy);
            Assert.Equal(2, busy.BusySlots.Count);
            Assert.Contains(busy.BusySlots, slot => slot.HolderLabel == expectedAttemptLabel);
            Assert.Contains(busy.BusySlots, slot => slot.HolderLabel == "cohort-gate:goal-aaaaaaaa+bbbbbbbb");
            Assert.NotNull(busyOutput);
            Assert.Contains("SLOTS_BUSY wantedBy=first-available-stable-slot", busyOutput);
            foreach (var slot in busy.BusySlots)
                Assert.Contains($"slot-{slot.SlotIndex}:pid-{Environment.ProcessId}:holder-{slot.HolderLabel}", busyOutput);
        }
        finally { DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyMetadataWithoutLabelPrintsUnknown(bool legacyPid)
    {
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () =>
            new ProcessCommandLineSnapshot(new Dictionary<int, string>());
        try
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.Zero, slotCount: 1, storageRoot: StorageRoot, holderLabel: "original-holder");
            File.WriteAllText(lease.Environment.ExecutionLockPath + ".owner.json", legacyPid
                ? Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : JsonSerializer.Serialize(new
                {
                    version = 1, leaseId = lease.Environment.LeaseId, slotOwnerToken = lease.Environment.SlotOwnerToken,
                    artifactsPath = lease.Environment.ArtifactsPath, ownerProcessId = Environment.ProcessId,
                    machineName = Environment.MachineName, acquiredAt = DateTimeOffset.UtcNow
                }));
            DotnetBuildSlotsBusyException? error = null;
            var output = AsyncLocalConsoleRouter.Capture(() => error = Assert.Throws<DotnetBuildSlotsBusyException>(() =>
                DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.Zero, slotCount: 1, storageRoot: StorageRoot)));

            Assert.NotNull(error);
            Assert.Null(Assert.Single(error.SlotsBusy.BusySlots).HolderLabel);
            Assert.Contains($"slot-0:pid-{Environment.ProcessId}:holder-unknown", output);
            Assert.Contains("wantedBy=first-available-stable-slot", output);
        }
        finally { DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null; }
    }

    [Fact]
    public void HolderDiagnosticsKeepLabelsInOneToken()
    {
        var text = DotnetBuildEnvironmentManager.FormatBusySlots(
            [new(0, 123) { HolderLabel = "cohort-gate:goal-a\nSLOTS_BUSY x=y|extra" }, new(1, null)]);
        Assert.Equal("slot-0:pid-123:holder-cohort-gate:goal-a_SLOTS_BUSY_x_y_extra|slot-1:pid-unknown:holder-unknown", text);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("cohort-gate:goal-aaaaaaaa+bbbbbbbb", "cohort-gate:goal-aaaaaaaa+bbbbbbbb")]
    public void HolderDiagnosticsPreserveNativeFailureFields(string? holderLabel, string expectedLabel)
    {
        var text = DotnetBuildEnvironmentManager.FormatBusySlots(
            [new(0, null, UnavailableStatus: ProcessInspectionStatus.NativeFailure,
                NativeError: 24, FailureOperation: "CreateToolhelp32Snapshot") { HolderLabel = holderLabel }]);

        Assert.Equal("slot-0:pid-unknown:status-NativeFailure:native-error-24:" +
            $"operation-CreateToolhelp32Snapshot:holder-{expectedLabel}", text);
    }

    [Fact]
    public void HolderDiagnosticsPreserveUnavailableProcessFields()
    {
        var text = DotnetBuildEnvironmentManager.FormatBusySlots(
            [new(0, 123, 456, "dotnet", ProcessInspectionStatus.AccessDenied, 5, "OpenProcess")
                { HolderLabel = "parallel-acceptance:goal-aaaaaaaa:attempt-bbbbbbbb" }]);

        Assert.Equal("slot-0:pid-123:unavailable-pid-456:name-dotnet:status-AccessDenied:" +
            "native-error-5:operation-OpenProcess:holder-parallel-acceptance:goal-aaaaaaaa:attempt-bbbbbbbb", text);
    }

    private static string? ReadLabel(DotnetBuildEnvironment environment)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(environment.ExecutionLockPath + ".owner.json"));
        return document.RootElement.GetProperty("holderLabel").GetString();
    }
}
