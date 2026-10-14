using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each control owns its child handles, named readiness event, SQLite,
// repository and goal artifacts. No machine-wide process census or environment mutation.
public sealed class CompletionBuildEvidenceLaunchTests
{
    [Fact]
    public void TransientRegistration_AdmitsSameChildAfterDurableCheckpoint()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        using var child = new SacrificialChild();
        var reads = 0;
        var delays = new List<int>();
        var coordinator = fixture.Coordinator(child.Start, process =>
            SpawnProcessIdentityReader.ReadForRegistration(process, candidate =>
            {
                Assert.Same(child.Process, candidate);
                return ++reads == 1 ? null : child.Identity;
            }, delays.Add, 3, 17));

        fixture.Runner(coordinator).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.Pending, operation.Phase);
        Assert.Equal(1, child.Starts);
        Assert.Equal(2, reads);
        Assert.Equal(new[] { 17 }, delays);
        Assert.Equal(child.Identity!.ProcessId, operation.ChildPid);
        Assert.Equal(child.Identity.StartedAt, operation.ChildStartedAt);
        Assert.Equal(child.Identity.ImagePath, operation.ChildImagePath);
        Assert.True(child.Observer!.WaitForExit(10000), "Authorized sacrificial child did not exit.");
        coordinator.Cancel(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        using var metadata = ReadOnlyMetadata(operation);
        Assert.True(metadata.RootElement.GetProperty("Authorized").GetBoolean());
        Assert.Equal(2, metadata.RootElement.GetProperty("Registration").GetProperty("Attempts").GetInt32());
        Assert.Empty(metadata.RootElement.GetProperty("Errors").EnumerateArray());
        Assert.Contains("permit:" + operation.Id, ReadStream(operation, "stdout"));
        Assert.DoesNotContain("permit:", ReadStream(operation, "stderr"));
        Assert.False(File.Exists(operation.ResultPath));
    }

    [Theory]
    [InlineData("unavailable", "reason=identity-unavailable", 3)]
    [InlineData("exited", "reason=process-exited", 1)]
    [InlineData("reader-throws", "injected registration exception", 0)]
    [InlineData("valid-but-exited", "exited or registration described a different child", 1)]
    [InlineData("foreign-identity", "exited or registration described a different child", 1)]
    public void RejectedAdmission_RetainsActualStreamsAndNeverWritesPermit(
        string control, string reason, int attempts)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        using var child = new SacrificialChild(exitBeforeAdmission: control is "exited" or "valid-but-exited");
        var coordinator = fixture.Coordinator(child.Start, process => control switch
        {
            "reader-throws" => throw new IOException("injected registration exception"),
            "valid-but-exited" => new(child.Identity, 1, "status=read attempts=1"),
            "foreign-identity" => new(child.Identity! with { ProcessId = child.Identity.ProcessId + 1 }, 1, "status=read attempts=1"),
            _ => SpawnProcessIdentityReader.ReadForRegistration(process, _ => null, _ => { }, 3, 0)
        });

        fixture.Runner(coordinator).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, operation.Phase);
        Assert.Null(operation.ChildPid);
        Assert.Null(operation.BuildExitCode);
        Assert.Contains(reason, operation.Diagnostic);
        Assert.True(child.Observer!.HasExited);
        Assert.Equal(1, child.Starts);
        Assert.Contains("stdout:" + child.Marker, ReadStream(operation, "stdout"));
        Assert.DoesNotContain("stderr:", ReadStream(operation, "stdout"));
        Assert.Contains("stderr:" + child.Marker, ReadStream(operation, "stderr"));
        Assert.DoesNotContain("stdout:", ReadStream(operation, "stderr"));
        Assert.DoesNotContain("permit:", ReadStream(operation, "stdout"));
        using var metadata = ReadOnlyMetadata(operation);
        Assert.False(metadata.RootElement.GetProperty("Authorized").GetBoolean());
        Assert.Equal("exited", metadata.RootElement.GetProperty("Exit").GetString());
        Assert.Equal(child.Observer.ExitCode, metadata.RootElement.GetProperty("ExitCode").GetInt32());
        var registration = metadata.RootElement.GetProperty("Registration");
        if (attempts == 0) Assert.Equal(JsonValueKind.Null, registration.ValueKind);
        else Assert.Equal(attempts, registration.GetProperty("Attempts").GetInt32());
        Assert.False(File.Exists(operation.ResultPath));
        Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
    }

    [Fact]
    public void FailedCheckpoint_RetainsAdmittedChildDiagnosticsWithoutPermit()
    {
        var fixture = new CompletionBuildEvidenceFixture { FailChildCheckpoint = true };
        using var child = new SacrificialChild();
        var coordinator = fixture.Coordinator(child.Start);
        fixture.Runner(coordinator).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, operation.Phase);
        Assert.Contains("injected child identity checkpoint failure", operation.Diagnostic);
        Assert.True(child.Observer!.HasExited);
        using var metadata = ReadOnlyMetadata(operation);
        Assert.False(metadata.RootElement.GetProperty("Authorized").GetBoolean());
        Assert.Contains(child.Marker, ReadStream(operation, "stdout"));
        Assert.Contains(child.Marker, ReadStream(operation, "stderr"));
        Assert.DoesNotContain("permit:", ReadStream(operation, "stdout"));
        Assert.Null(operation.BuildExitCode);
    }

    [Fact]
    public void ExitBeforePermit_FailsClosedAfterIdentityCheckpoint()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        using var child = new SacrificialChild();
        fixture.BeforeChildCheckpoint = () =>
        {
            child.Process!.StandardInput.Close();
            Assert.True(child.Observer!.WaitForExit(10000), "Child did not exit before the authorization gate.");
        };
        var coordinator = fixture.Coordinator(child.Start);
        fixture.Runner(coordinator).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, operation.Phase);
        Assert.Contains("no longer live for authorization", operation.Diagnostic);
        Assert.Equal(child.Identity!.ProcessId, operation.ChildPid);
        Assert.DoesNotContain("permit:", ReadStream(operation, "stdout"));
        using var metadata = ReadOnlyMetadata(operation);
        Assert.False(metadata.RootElement.GetProperty("Authorized").GetBoolean());
        Assert.Null(operation.BuildExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmittedDrainFailure_CannotApplyMatchingSuccessResult(bool restart)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        using var child = new SacrificialChild();
        var coordinator = fixture.Coordinator(child.Start,
            hostReaders: process => (process.StandardOutput, new FailedReader()));
        var runner = fixture.Runner(coordinator);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.True(child.Observer!.WaitForExit(10000), "Authorized drain-failure child did not exit.");
        if (restart)
        {
            Assert.True(SpinWait.SpinUntil(() => MetadataFiles(fixture.Task.CompletionBuildEvidence!).Length == 1,
                TimeSpan.FromSeconds(10)), "Admitted child's completed diagnostic artifact was not published.");
            fixture.Reload();
            runner = fixture.Runner(); // New coordinator has no in-memory capture authority.
        }
        fixture.Release(); // Matching terminal receipt alone cannot hide a capture apparatus fault.
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, operation.Phase);
        Assert.Null(operation.BuildExitCode);
        Assert.Contains("injected stream read failure", operation.Diagnostic);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        using var metadata = ReadOnlyMetadata(operation);
        Assert.True(metadata.RootElement.GetProperty("Authorized").GetBoolean());
        Assert.Contains("status=read-failed", metadata.RootElement.GetProperty("StandardError").GetString());
        Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureOwnedDeadline_PreservesTimedOutAfterChildExit(bool restart)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        using var child = new SacrificialChild(holdAfterPermit: true);
        var checkpoint = false;
        var readsAfterCheckpoint = 0;
        fixture.BeforeChildCheckpoint = () => checkpoint = true;
        var coordinator = fixture.Coordinator(child.Start, utcNow: () =>
            // Authorization sees a live deadline; the capture's subsequent observation
            // sees an expired deadline. Setup and child readiness precede this stimulus.
            checkpoint && Interlocked.Increment(ref readsAfterCheckpoint) > 1
                ? fixture.Clock.UtcNow.AddHours(1) : fixture.Clock.UtcNow);
        var runner = fixture.Runner(coordinator);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var admitted = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.Pending, admitted.Phase);
        Assert.True(child.Observer!.WaitForExit(10000), "Capture deadline did not reap its held child.");
        Assert.True(SpinWait.SpinUntil(() => MetadataFiles(admitted).Length == 1,
            TimeSpan.FromSeconds(15)), "Capture deadline evidence was not published.");
        using var metadata = ReadOnlyMetadata(admitted);
        Assert.True(metadata.RootElement.GetProperty("Authorized").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("DeadlineExceeded").GetBoolean());
        Assert.Equal("exited", metadata.RootElement.GetProperty("Exit").GetString());
        if (restart)
        {
            fixture.Reload();
            runner = fixture.Runner();
        }
        fixture.Release(); // Even a matching success receipt must not erase the typed timeout.
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.TimedOut, operation.Phase);
        Assert.Contains("deadline", operation.Diagnostic);
        Assert.Null(operation.BuildExitCode);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.False(fixture.Task.LastVerification!.Succeeded);
        Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
        fixture.Reload();
        Assert.Equal(CompletionBuildEvidencePhase.TimedOut, fixture.Task.CompletionBuildEvidence!.Phase);
    }

    [Theory]
    [InlineData("read", "injected stream read failure")]
    [InlineData("drain", "capture incomplete")]
    [InlineData("artifact", "Host metadata write failed")]
    public void AdmittedDiagnosticFault_CancellationPersistsAndReleasesLease(string fault, string reason)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        using var child = new SacrificialChild(holdAfterPermit: true);
        var blocked = new CancellationReader();
        string? blockedDirectory = null;
        var coordinator = fixture.Coordinator(child.Start, process =>
        {
            if (fault == "artifact")
            {
                blockedDirectory = Path.GetDirectoryName(fixture.Task.CompletionBuildEvidence!.ResultPath)!;
                Directory.CreateDirectory(Path.GetDirectoryName(blockedDirectory)!);
                File.WriteAllText(blockedDirectory, "controlled directory obstruction");
            }
            return SpawnProcessIdentityReader.ReadForRegistration(process);
        }, process => (fault switch
        {
            "read" => new FailedReader(),
            "drain" => blocked,
            _ => (TextReader)process.StandardOutput
        }, process.StandardError));
        try
        {
            var runner = fixture.Runner(coordinator);
            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
            var admitted = fixture.Task.CompletionBuildEvidence!;
            Assert.Equal(CompletionBuildEvidencePhase.Pending, admitted.Phase);
            Assert.NotNull(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
            runner.CancelLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
            var operation = fixture.Task.CompletionBuildEvidence!;
            Assert.True(child.Observer!.HasExited);
            Assert.Equal(CompletionBuildEvidencePhase.Cancelled, operation.Phase);
            Assert.Contains("Operator cancelled", operation.Diagnostic);
            Assert.Contains(reason, operation.Diagnostic);
            Assert.Null(operation.BuildExitCode);
            Assert.Equal(WorkTaskStatus.Cancelled, fixture.Task.Status);
            Assert.True(fixture.Task.LastProcess!.WasCancelled);
            Assert.Null(fixture.Task.LastVerification);
            Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
            if (fault == "drain")
            {
                Assert.True(blocked.Entered);
                Assert.True(blocked.Cancelled.Task.IsCompletedSuccessfully);
            }
            if (fault != "artifact")
            {
                using var metadata = ReadOnlyMetadata(operation);
                Assert.True(metadata.RootElement.GetProperty("Authorized").GetBoolean());
                Assert.Equal("exited", metadata.RootElement.GetProperty("Exit").GetString());
                Assert.False(metadata.RootElement.GetProperty("DeadlineExceeded").GetBoolean());
                Assert.NotEmpty(metadata.RootElement.GetProperty("Errors").EnumerateArray());
            }
            fixture.Reload();
            Assert.Equal(CompletionBuildEvidencePhase.Cancelled, fixture.Task.CompletionBuildEvidence!.Phase);
            Assert.Contains(reason, fixture.Task.CompletionBuildEvidence.Diagnostic);
        }
        finally { if (blockedDirectory is not null) File.Delete(blockedDirectory); }
    }

    [Fact]
    public void StartException_RecordsNoChildAndUnavailableStreams()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var coordinator = fixture.Coordinator(_ => throw new IOException("injected process start exception"));
        fixture.Runner(coordinator).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, operation.Phase);
        Assert.Contains("injected process start exception", operation.Diagnostic);
        Assert.Null(operation.ChildPid);
        Assert.Null(operation.BuildExitCode);
        using var metadata = ReadOnlyMetadata(operation);
        Assert.Equal("no-child", metadata.RootElement.GetProperty("Exit").GetString());
        Assert.Contains("unavailable", metadata.RootElement.GetProperty("StandardOutput").GetString());
        Assert.Contains("unavailable", metadata.RootElement.GetProperty("StandardError").GetString());
        Assert.False(metadata.RootElement.GetProperty("Authorized").GetBoolean());
        Assert.Empty(StreamFiles(operation, "stdout"));
        Assert.Empty(StreamFiles(operation, "stderr"));
        Assert.False(File.Exists(operation.ResultPath));
    }

    [Fact]
    public void ReadFailure_PreservesOtherStreamAndAdmissionReason()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        using var child = new SacrificialChild();
        var coordinator = fixture.Coordinator(child.Start,
            process => SpawnProcessIdentityReader.ReadForRegistration(process, _ => null, _ => { }, 2, 0),
            process => (new FailedReader(), process.StandardError));
        fixture.Runner(coordinator).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, operation.Phase);
        Assert.Contains("reason=identity-unavailable", operation.Diagnostic);
        Assert.Contains("injected stream read failure", operation.Diagnostic);
        Assert.True(child.Observer!.HasExited);
        Assert.Contains("stderr:" + child.Marker, ReadStream(operation, "stderr"));
        using var metadata = ReadOnlyMetadata(operation);
        Assert.Contains("status=read-failed", metadata.RootElement.GetProperty("StandardOutput").GetString());
        Assert.False(metadata.RootElement.GetProperty("Authorized").GetBoolean());
        Assert.Null(operation.BuildExitCode);
    }

    [Fact]
    public void BlockedDrain_IsCancelledAndObservedOnAdmissionFailure()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        using var child = new SacrificialChild();
        var blocked = new CancellationReader();
        var coordinator = fixture.Coordinator(child.Start,
            process => SpawnProcessIdentityReader.ReadForRegistration(process, _ => null, _ => { }, 2, 0),
            process => (blocked, process.StandardError));
        fixture.Runner(coordinator).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.True(blocked.Entered);
        Assert.True(blocked.Cancelled.Task.IsCompletedSuccessfully);
        Assert.True(child.Observer!.HasExited);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, operation.Phase);
        Assert.Contains("reason=identity-unavailable", operation.Diagnostic);
        using var metadata = ReadOnlyMetadata(operation);
        Assert.Contains("status=cancelled", metadata.RootElement.GetProperty("StandardOutput").GetString());
        Assert.Contains("stderr:" + child.Marker, ReadStream(operation, "stderr"));
        Assert.False(metadata.RootElement.GetProperty("Authorized").GetBoolean());
    }

    [Fact]
    public void ArtifactWriteFailure_StillReapsChildAndKeepsPrimaryReason()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        using var child = new SacrificialChild();
        string? blockedDirectory = null;
        var coordinator = fixture.Coordinator(child.Start, process =>
        {
            blockedDirectory = Path.GetDirectoryName(fixture.Task.CompletionBuildEvidence!.ResultPath)!;
            Directory.CreateDirectory(Path.GetDirectoryName(blockedDirectory)!);
            File.WriteAllText(blockedDirectory, "controlled directory obstruction");
            return SpawnProcessIdentityReader.ReadForRegistration(process, _ => null, _ => { }, 2, 0);
        });
        try
        {
            fixture.Runner(coordinator).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
            var operation = fixture.Task.CompletionBuildEvidence!;
            Assert.True(child.Observer!.HasExited);
            Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, operation.Phase);
            Assert.Contains("reason=identity-unavailable", operation.Diagnostic);
            Assert.Contains("Artifact directory unavailable", operation.Diagnostic);
            Assert.Contains("Host metadata write failed", operation.Diagnostic);
            Assert.Null(operation.BuildExitCode);
            Assert.False(File.Exists(operation.ResultPath));
        }
        finally { if (blockedDirectory is not null) File.Delete(blockedDirectory); }
    }

    [Fact]
    public void LeaseWaitRelaunch_KeepsAttemptStreamsAndIdentitiesSeparate()
    {
        var fixture = new CompletionBuildEvidenceFixture { BusyLease = true };
        using var first = new SacrificialChild();
        using var second = new SacrificialChild();
        var starts = 0;
        var coordinator = fixture.Coordinator(info => (++starts == 1 ? first : second).Start(info));
        var runner = fixture.Runner(coordinator);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.WaitingForLease, operation.Phase);
        Assert.True(first.Observer!.HasExited);
        var firstMetadata = Assert.Single(MetadataFiles(operation));
        var firstOutput = Path.ChangeExtension(firstMetadata, "stdout");
        var original = File.ReadAllText(firstOutput);
        Assert.Contains(first.Marker, original);
        Assert.DoesNotContain("permit:", original);

        fixture.BusyLease = false;
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var admitted = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(operation.Id, admitted.Id);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, admitted.Phase);
        Assert.Equal(second.Identity!.ProcessId, admitted.ChildPid);
        Assert.True(second.Observer!.WaitForExit(10000), "Second authorized child did not exit.");
        coordinator.Cancel(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(2, starts);
        Assert.Equal(original, File.ReadAllText(firstOutput));
        var files = MetadataFiles(operation);
        Assert.Equal(2, files.Length);
        var secondMetadata = Assert.Single(files.Where(file => file != firstMetadata));
        using var firstEvidence = JsonDocument.Parse(File.ReadAllText(firstMetadata));
        using var secondEvidence = JsonDocument.Parse(File.ReadAllText(secondMetadata));
        Assert.NotEqual(firstEvidence.RootElement.GetProperty("AttemptId").GetString(),
            secondEvidence.RootElement.GetProperty("AttemptId").GetString());
        Assert.False(firstEvidence.RootElement.GetProperty("Authorized").GetBoolean());
        Assert.True(secondEvidence.RootElement.GetProperty("Authorized").GetBoolean());
        var secondOutput = File.ReadAllText(Path.ChangeExtension(secondMetadata, "stdout"));
        Assert.Contains("permit:" + operation.Id, secondOutput);
        Assert.DoesNotContain(first.Marker, secondOutput);
        Assert.False(File.Exists(operation.ResultPath));
    }

    private static string[] MetadataFiles(CompletionBuildEvidenceOperation operation) =>
        StreamFiles(operation, "json");

    private static string[] StreamFiles(CompletionBuildEvidenceOperation operation, string suffix)
    {
        var directory = Path.GetDirectoryName(operation.ResultPath)!;
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, Path.GetFileName(operation.ResultPath) + ".host.*." + suffix) : [];
    }

    private static JsonDocument ReadOnlyMetadata(CompletionBuildEvidenceOperation operation) =>
        JsonDocument.Parse(File.ReadAllText(Assert.Single(MetadataFiles(operation))));

    private static string ReadStream(CompletionBuildEvidenceOperation operation, string suffix) =>
        File.ReadAllText(Assert.Single(StreamFiles(operation, suffix)));

    private sealed class FailedReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("injected stream read failure"));
    }

    private sealed class CancellationReader : TextReader
    {
        internal bool Entered;
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _unreleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Entered = true;
            try { return await _unreleased.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
    }

    private sealed class SacrificialChild(bool exitBeforeAdmission = false, bool holdAfterPermit = false) : IDisposable
    {
        internal readonly string Marker = Guid.NewGuid().ToString("N");
        internal Process? Process;
        internal Process? Observer;
        internal SpawnProcessIdentity? Identity;
        internal int Starts;
        private EventWaitHandle? _hold;

        internal Process Start(ProcessStartInfo requested)
        {
            Assert.False(requested.UseShellExecute);
            Assert.True(requested.CreateNoWindow);
            Assert.True(requested.RedirectStandardInput && requested.RedirectStandardOutput && requested.RedirectStandardError);
            Starts++;
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Marker);
            if (holdAfterPermit) _hold = new EventWaitHandle(false, EventResetMode.ManualReset, Marker + "-hold");
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = requested.WorkingDirectory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP" })
                if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add($"[Console]::Out.WriteLine('stdout:{Marker}'); [Console]::Error.WriteLine('stderr:{Marker}'); " +
                "[Console]::Out.Flush(); [Console]::Error.Flush(); " +
                $"$ready = [Threading.EventWaitHandle]::OpenExisting('{Marker}'); $ready.Set() | Out-Null; " +
                "$value = [Console]::ReadLine(); if ($null -ne $value) { [Console]::Out.WriteLine('permit:' + $value) }; " +
                (holdAfterPermit ? $"$hold = [Threading.EventWaitHandle]::OpenExisting('{Marker}-hold'); $hold.WaitOne() | Out-Null; " : "") +
                "exit 7");
            Process = System.Diagnostics.Process.Start(info)!;
            Observer = System.Diagnostics.Process.GetProcessById(Process.Id);
            // GetProcessById opens its handle lazily. Retain it while the child waits on stdin,
            // so ExitCode remains readable after capture reaps and disposes its own process.
            _ = Observer.SafeHandle;
            Assert.True(ready.WaitOne(TimeSpan.FromSeconds(10)), "Sacrificial child did not emit its readiness event.");
            // The exited control uses a cached complete tuple and an observed exit, never a startup race.
            Identity = SpawnProcessIdentityReader.ReadForRegistration(Process).Identity;
            Assert.NotNull(Identity);
            if (exitBeforeAdmission)
            {
                Process.StandardInput.Close();
                Assert.True(Observer.WaitForExit(10000), "Exited admission control did not exit.");
            }
            return Process;
        }

        public void Dispose()
        {
            if (Observer is not null)
            {
                if (!Observer.HasExited) Observer.Kill(entireProcessTree: true);
                Assert.True(Observer.WaitForExit(10000), "Fixture child cleanup did not exit.");
                Observer.Dispose();
            }
            Process?.Dispose();
            _hold?.Dispose();
        }
    }
}
