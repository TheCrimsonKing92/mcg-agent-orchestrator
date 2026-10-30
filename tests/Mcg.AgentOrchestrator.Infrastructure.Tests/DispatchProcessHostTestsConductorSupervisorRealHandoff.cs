using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class DispatchProcessHostTestsConductorSupervisorRealHandoff(Xunit.ITestOutputHelper output)
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(30);

    [Xunit.Fact]
    public void LiveTickLogAllowsActiveWriterAndCountsOnlyCompleteLines()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-live-ticks-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            writer.WriteLine("TICK_END tick=1 activation=true");
            Assert.Equal(1, CountTicks(path));
            writer.Write("TICK_END tick=2 activation=true");
            Assert.Equal(1, CountTicks(path));
            writer.WriteLine();
            Assert.Equal(2, CountTicks(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Xunit.Fact]
    public void Cleanup_LaunchReceiptBeforeReadiness_StopsExactSuccessor()
    {
        var continuity = Path.Combine(Path.GetTempPath(), "mcg-cleanup-launch-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(continuity, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            var incumbent = new ConductorSupervisorProcessIdentity(101, DateTimeOffset.Parse("2026-10-13T11:00:00Z"));
            var successor = new ConductorSupervisorProcessIdentity(102, incumbent.StartedAt.AddSeconds(1));
            var decoy = new ConductorSupervisorProcessIdentity(103, successor.StartedAt.AddSeconds(1));
            string Receipt(ConductorSupervisorProcessIdentity owner, ConductorSupervisorProcessIdentity child) =>
                JsonSerializer.Serialize(new
                {
                    eventKind = "loop-handoff-launch-authority",
                    detail = JsonSerializer.Serialize(new
                    {
                        nativeError = 0, successorPid = child.ProcessId, successorStartedAt = child.StartedAt,
                        authority = new { incumbentPid = owner.ProcessId, incumbentStartedAt = owner.StartedAt }
                    })
                }) + Environment.NewLine;
            File.WriteAllText(Path.Combine(continuity, "conduct-events.log"), Receipt(incumbent, decoy));
            File.WriteAllText(Path.Combine(logs, "conduct-events.log"),
                Receipt(incumbent with { StartedAt = incumbent.StartedAt.AddMinutes(-1) }, decoy) +
                Receipt(incumbent, successor));
            Assert.Empty(Directory.GetFiles(logs, "supervisor-ready-*.json"));
            var stopped = new List<ConductorSupervisorProcessIdentity>();

            StopFixtureSuccessors(continuity, null, incumbent, stopped.Add);

            Assert.Equal(successor, Assert.Single(stopped));
        }
        finally
        {
            Directory.Delete(continuity, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Cleanup_MissingLaunchReceipt_StopsReadySuccessor()
    {
        var continuity = Path.Combine(Path.GetTempPath(), "mcg-cleanup-ready-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(continuity, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            var incumbent = new ConductorSupervisorProcessIdentity(101, DateTimeOffset.Parse("2026-10-13T11:00:00Z"));
            var successor = new ConductorSupervisorProcessIdentity(102, incumbent.StartedAt.AddSeconds(1));
            var ready = new ConductorSupervisorReadyRecord("fixture", successor,
                new ConductorSupervisorBuildIdentity("fixture-sha", continuity));
            File.WriteAllText(Path.Combine(logs, "supervisor-ready-fixture.json"), JsonSerializer.Serialize(ready));
            File.WriteAllText(Path.Combine(continuity, "supervisor-ready-decoy.json"),
                JsonSerializer.Serialize(ready with { Successor = successor with { ProcessId = 103 } }));
            Assert.False(File.Exists(Path.Combine(logs, "conduct-events.log")));
            var stopped = new List<ConductorSupervisorProcessIdentity>();

            StopFixtureSuccessors(continuity, null, incumbent, stopped.Add);

            Assert.Equal(successor, Assert.Single(stopped));
        }
        finally
        {
            Directory.Delete(continuity, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Cleanup_DiagnosticFailureReceipt_RecoversExactSuccessor(bool useReadiness)
    {
        var continuity = Path.Combine(Path.GetTempPath(), "mcg-cleanup-diagnostics-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(continuity, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            var incumbent = new ConductorSupervisorProcessIdentity(101, DateTimeOffset.Parse("2026-10-13T11:00:00Z"));
            var successor = new ConductorSupervisorProcessIdentity(102, incumbent.StartedAt.AddSeconds(1));
            var decoy = new ConductorSupervisorProcessIdentity(103, successor.StartedAt.AddSeconds(1));
            string Receipt(ConductorSupervisorProcessIdentity child, object authority) =>
                JsonSerializer.Serialize(new
                {
                    eventKind = "loop-handoff-launch-authority",
                    detail = JsonSerializer.Serialize(new
                    {
                        nativeError = 0, successorPid = child.ProcessId, successorStartedAt = child.StartedAt,
                        authority
                    })
                }) + Environment.NewLine;
            var receipts = Receipt(decoy, new { diagnosticFailure = "InvalidOperationException:fixture" });
            if (useReadiness)
            {
                var ready = new ConductorSupervisorReadyRecord("fixture", successor,
                    new ConductorSupervisorBuildIdentity("fixture-sha", continuity));
                File.WriteAllText(Path.Combine(logs, "supervisor-ready-fixture.json"), JsonSerializer.Serialize(ready));
            }
            else
            {
                receipts += Receipt(successor, new
                {
                    incumbentPid = incumbent.ProcessId, incumbentStartedAt = incumbent.StartedAt
                });
                Assert.Empty(Directory.GetFiles(logs, "supervisor-ready-*.json"));
            }
            File.WriteAllText(Path.Combine(logs, "conduct-events.log"), receipts);
            var stopped = new List<ConductorSupervisorProcessIdentity>();

            StopFixtureSuccessors(continuity, null, incumbent, stopped.Add);

            Assert.Equal(successor, Assert.Single(stopped));
        }
        finally
        {
            Directory.Delete(continuity, recursive: true);
        }
    }

    [Xunit.Fact(Skip = "Requires Windows process/job semantics.", SkipUnless = nameof(IsWindows))]
    public async Task RealSupervisorTransfersHealthyChildAndSuccessorTicksAfterPredecessorBoundaryCloses()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-real-supervisor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var continuity = Path.Combine(root, ".orchestrator", "continuity");
        var token = @"Local\mcg-real-supervisor-" + Guid.NewGuid().ToString("N");
        using var handoff = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-handoff");
        using var outer = new DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority.FixtureJob(token + "-outer", true);
        Process? successor = null;
        ConductorSupervisorProcessIdentity? successorIdentity = null;
        ConductorSupervisorProcessIdentity? predecessorIdentity = null;
        var completed = false;
        var handoffTimeout = TimeSpan.FromMinutes(1);
        var adoptedRenewalSeconds = 60;
        var childExitTimeout = TimeSpan.FromSeconds(adoptedRenewalSeconds) + ObservationTimeout;
        var pipeDrainTimeout = TimeSpan.FromSeconds(15);
        // The custodian watchdog must outlive every phase before its expected exit.
        var custodianWatchdog = handoffTimeout + ObservationTimeout + ObservationTimeout +
            childExitTimeout + pipeDrainTimeout + TimeSpan.FromSeconds(30);
        try
        {
            var probe = DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority.LocateProbe(GetType());
            Assert.True(probe.Directory is not null, "Probe discovery failed. Tried: " + string.Join(", ", probe.TriedPaths));
            var closure = probe.Directory!;
            var app = Path.Combine(closure, "Mcg.AgentOrchestrator.App.dll");
            output.WriteLine(JsonSerializer.Serialize(new { root, closure, probe.TriedPaths,
                candidateSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(app))),
                candidateMvid = typeof(ConductorContinuitySupervisor).Module.ModuleVersionId,
                outerJob = outer.Name, outerLimits = outer.Limits,
                runnerAncestor = "Operator must enable and record -AllowBreakaway; other ancestors are unknown" }));
            CopyClosure(closure, Path.Combine(root, "stage-a"));
            CopyClosure(closure, Path.Combine(root, "stage-b"));
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            var allowed = new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA",
                "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86" };
            info.Environment.Clear();
            foreach (var key in allowed)
                if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
            info.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = root;
            info.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(root, "registry");
            // Maintenance runs in real children. Keep its discovery inputs inside
            // this disposable scenario rather than scanning the operator's stores.
            var buildRoot = Path.Combine(root, "build-storage");
            var tempRoot = Path.Combine(root, "temp");
            var localAppData = Path.Combine(root, "local-app-data");
            Directory.CreateDirectory(buildRoot);
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(localAppData);
            info.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = buildRoot;
            info.Environment["TEMP"] = tempRoot;
            info.Environment["TMP"] = tempRoot;
            info.Environment["LOCALAPPDATA"] = localAppData;
            info.Environment["MCG_ORCHESTRATOR_MAX_DURATION_RESTAGE"] = "0";
            info.Environment["MCG_ORCHESTRATOR_ACTIVATION_HEALTHY_TICKS"] = "3";
            foreach (var arg in new[] { Path.Combine(closure, "Mcg.AgentOrchestrator.SupervisorLaunchProbe.dll"), "supervisor", root, token,
                adoptedRenewalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                custodianWatchdog.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                info.ArgumentList.Add(arg);
            using var predecessor = OwnedProcessGroup.StartSuspended(info);
            predecessorIdentity = new(predecessor.Process.Id, new DateTimeOffset(predecessor.Process.StartTime.ToUniversalTime()));
            outer.Add(predecessor.Process);
            Assert.True(outer.Contains(predecessor.Process));
            predecessor.Resume();
            Assert.True(handoff.WaitOne(handoffTimeout), "Real supervisor did not publish a completed handoff; inspect retained fixture logs.");
            var storageReceipt = File.ReadAllText(Path.Combine(root, "fixture-storage.json"));
            output.WriteLine(storageReceipt);
            using var storage = JsonDocument.Parse(storageReceipt);
            Assert.Equal(buildRoot, storage.RootElement.GetProperty("buildRoot").GetString());
            Assert.Equal(Path.TrimEndingDirectorySeparator(tempRoot),
                Path.TrimEndingDirectorySeparator(storage.RootElement.GetProperty("tempRoot").GetString()!));
            Assert.Equal(Path.Combine(localAppData, "Temp", "Low", "mcg-tests"),
                storage.RootElement.GetProperty("mtpResultsRoot").GetString());
            Assert.Equal(adoptedRenewalSeconds, storage.RootElement.GetProperty("adoptedRenewalSeconds").GetInt32());
            Assert.Equal(custodianWatchdog.TotalSeconds, storage.RootElement.GetProperty("custodianWatchdogSeconds").GetDouble());
            var receipt = File.ReadAllText(Path.Combine(root, "handoff.json"));
            output.WriteLine(receipt);
            using var handoffDocument = JsonDocument.Parse(receipt);
            var detail = handoffDocument.RootElement.GetProperty("detail").GetString()!;
            using var transferred = JsonDocument.Parse(detail["SUPERVISOR_HANDOFF ".Length..]);
            var payload = transferred.RootElement;
            successorIdentity = new(payload.GetProperty("successorPid").GetInt32(), payload.GetProperty("successorStartedAt").GetDateTimeOffset());
            successor = Process.GetProcessById(successorIdentity.ProcessId);
            AssertIdentity(successor, successorIdentity);
            Assert.False(outer.Contains(successor));
            var recordPath = Assert.Single(Directory.GetFiles(continuity, "supervisor-handoff-*.json.claimed"));
            var record = JsonSerializer.Deserialize<ConductorSupervisorHandoffRecord>(File.ReadAllText(recordPath))!;
            Assert.Equal(predecessor.Process.Id, record.Incumbent.ProcessId);
            Assert.Equal(new DateTimeOffset(predecessor.Process.StartTime.ToUniversalTime()), record.Incumbent.StartedAt);
            var child = Assert.IsType<ConductorSupervisorActiveChild>(record.ActiveChild);
            Assert.Equal(child.Process.ProcessId, payload.GetProperty("childPid").GetInt32());
            Assert.Equal(child.Process.StartedAt, payload.GetProperty("childStartedAt").GetDateTimeOffset());
            using var healthyChild = Process.GetProcessById(child.Process.ProcessId);
            AssertIdentity(healthyChild, child.Process);
            var ticks = CountTicks(child.StdoutPath);
            Assert.True(ticks >= 3, "Adopted child did not emit the required three healthy activation ticks.");
            await WaitUntilAsync(() =>
            {
                AssertIdentity(healthyChild, child.Process);
                return CountTicks(child.StdoutPath) > ticks;
            }, "Same adopted child did not tick after lease transfer.");
            AssertIdentity(healthyChild, child.Process);
            output.WriteLine(JsonSerializer.Serialize(new { adoptedChild = child.Process, child.StdoutPath,
                ticksAtTransferObservation = ticks, ticksAfterTransfer = CountTicks(child.StdoutPath) }));
            await WaitUntilAsync(() => LeaseMatches(continuity, successorIdentity), "Exclusive lease did not identify the exact successor.");
            // First establish actual renewal of the same child. A live child at this
            // deadline is a different failure from a custodian failing to drain its pipes.
            var renewalWait = Stopwatch.StartNew();
            using (var renewalTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
            {
                renewalTimeout.CancelAfter(childExitTimeout);
                try
                {
                    await healthyChild.WaitForExitAsync(renewalTimeout.Token);
                }
                catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    AssertIdentity(healthyChild, child.Process);
                    output.WriteLine(JsonSerializer.Serialize(new { child = child.Process,
                        childExited = false, elapsedSeconds = renewalWait.Elapsed.TotalSeconds,
                        childExitTimeoutSeconds = childExitTimeout.TotalSeconds,
                        predecessorExited = predecessor.WaitForOwnedExit(TimeSpan.Zero),
                        exitArtifact = ConductorContinuityExitArtifact.TryRead(child.ExitArtifactPath),
                        child.StdoutPath, child.StderrPath }));
                    throw new TimeoutException("Adopted child did not exit within its renewal observation budget; pipe-custodian exit is not yet expected.");
                }
            }
            Assert.Equal(0, healthyChild.ExitCode);
            output.WriteLine(JsonSerializer.Serialize(new { child = child.Process, childExited = true,
                exitCode = healthyChild.ExitCode, elapsedSeconds = renewalWait.Elapsed.TotalSeconds }));
            // Only now measure bounded pipe drainage, without the child's lifetime.
            using (var drainTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
            {
                drainTimeout.CancelAfter(pipeDrainTimeout);
                try { await predecessor.WaitForOwnedExitAsync(drainTimeout.Token); }
                catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Pipe custodian did not exit after confirmed adopted child renewal.");
                }
            }
            Assert.Equal(0, predecessor.Process.ExitCode);
            // The successor consumes/deletes the exit artifact. The custodian has
            // now drained this exact child's log, so its terminal marker is durable.
            var renewalMarker = $"reason=max-duration seconds={adoptedRenewalSeconds}";
            Assert.Contains(ReadCompleteLiveLogLines(child.StdoutPath), line =>
                line.StartsWith("LOOP_STOP ", StringComparison.Ordinal) && line.Contains(renewalMarker, StringComparison.Ordinal));
            output.WriteLine(JsonSerializer.Serialize(new { child = child.Process, renewalMarker,
                predecessorExited = true, pipeDrainTimeoutSeconds = pipeDrainTimeout.TotalSeconds }));
            predecessor.Group.Dispose();
            outer.Dispose();
            AssertIdentity(successor, successorIdentity);
            var successorLogDirectory = Path.Combine(continuity, "logs");
            var successorLogPattern = $"supervisor-handoff-*-{record.Token}.out.log";
            var successorLogs = Directory.GetFiles(successorLogDirectory, successorLogPattern);
            Assert.True(successorLogs.Length == 1,
                $"Expected one successor log at {Path.Combine(successorLogDirectory, successorLogPattern)}; " +
                "found: " + string.Join(", ", Directory.GetFiles(successorLogDirectory, "supervisor-handoff-*.out.log")));
            var successorOut = successorLogs[0];
            output.WriteLine(JsonSerializer.Serialize(new { record.Token, successorIdentity, successorOut, boundaryClosed = true }));
            var successorTicks = CountTicks(successorOut);
            await WaitUntilAsync(() => CountTicks(successorOut) > successorTicks,
                "Real successor did not emit a new decision tick after predecessor exit and boundary closure.");
            AssertIdentity(successor, successorIdentity);
            await WaitUntilAsync(() => LeaseMatches(continuity, successorIdentity), "Successor lost its lease after predecessor boundary closure.");
            output.WriteLine("authorityTransferred=true adoptedChildTicked=true predecessorExited=true ownedPredecessorGroupClosed=true boundaryClosed=true successorTicked=true identityPreserved=true");
            File.WriteAllText(Path.Combine(root, ConductorBatchLoop.StopFileName), "fixture-stop");
            Assert.True(successor.WaitForExit(30000), "Real successor did not acknowledge fixture stop.");
            Assert.Equal(0, successor.ExitCode);
            completed = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, ConductorBatchLoop.StopFileName), "fixture-cleanup");
            StopFixtureSuccessors(continuity, successorIdentity, predecessorIdentity, ConductorLoopHandoff.StopFailedSuccessor);
            successor?.Dispose();
            outer.Dispose();
            foreach (var path in Directory.GetFiles(root, "*.log", SearchOption.AllDirectories)
                .Where(path => !path.Contains("stage-a", StringComparison.Ordinal) && !path.Contains("stage-b", StringComparison.Ordinal)))
                output.WriteLine(path + Environment.NewLine + ReadLiveLog(path));
            // Retain isolated stores/receipts on failure for Operator inspection.
            output.WriteLine("fixtureArtifacts=" + root + " retained=" + !completed);
            if (completed) Directory.Delete(root, recursive: true);
        }
    }

    private static void StopFixtureSuccessors(string continuity,
        ConductorSupervisorProcessIdentity? successor, ConductorSupervisorProcessIdentity? incumbent,
        Action<ConductorSupervisorProcessIdentity> stop)
    {
        var logs = Path.Combine(continuity, "logs");
        if (successor is null && incumbent is not null)
            successor = ReadLaunchedIdentity(logs, incumbent);
        if (successor is not null)
        {
            stop(successor);
            return;
        }
        if (!Directory.Exists(logs)) return;
        // A failure before handoff publication can still leave a ready process.
        // These artifacts belong solely to this disposable fixture directory.
        foreach (var readyPath in Directory.GetFiles(logs, "supervisor-ready-*.json"))
        {
            var ready = JsonSerializer.Deserialize<ConductorSupervisorReadyRecord>(File.ReadAllText(readyPath));
            if (ready is not null) stop(ready.Successor);
        }
    }

    private static void CopyClosure(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        SealClosure(destination);
    }

    private static void SealClosure(string directory, [CallerFilePath] string sourceFile = "")
    {
        var repositoryRoot = VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot)
            ? verifiedRoot : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        var resolver = Path.Combine(repositoryRoot, "scripts", "resolve-run-dir.ps1").Replace("'", "''");
        var folder = directory.Replace("'", "''");
        // Use the launcher's snapshot function so the real, handed-off supervisor
        // receives a sealed fixture with exactly the launcher's ordering and digest.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{resolver}}', [ref]$tokens, [ref]$errors)
            if ($errors.Count -ne 0) { throw 'Resolver parse failed' }
            $function = @($ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Get-OutputContentSnapshot'
            }, $true))
            if ($function.Count -ne 1) { throw 'Snapshot function not found' }
            . ([scriptblock]::Create($function[0].Extent.Text))
            $script:RunCacheMarkerName = '.mcg-run-closure.json'
            $snapshot = Get-OutputContentSnapshot -Path '{{folder}}' -ExcludeRunCacheMarker
            $marker = @{ schemaVersion = 2; digest = $snapshot.Digest; fileCount = $snapshot.FileCount }
            [IO.File]::WriteAllText((Join-Path '{{folder}}' $script:RunCacheMarkerName),
                ($marker | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
            """;
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell did not finish sealing the real supervisor fixture.");
        }
        Assert.True(process.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private static ConductorSupervisorProcessIdentity? ReadLaunchedIdentity(
        string directory, ConductorSupervisorProcessIdentity incumbent)
    {
        var path = Path.Combine(directory, "conduct-events.log");
        if (!File.Exists(path)) return null;
        foreach (var line in ReadCompleteLiveLogLines(path))
        {
            using var evt = JsonDocument.Parse(line);
            if (evt.RootElement.GetProperty("eventKind").GetString() != "loop-handoff-launch-authority") continue;
            using var launch = JsonDocument.Parse(evt.RootElement.GetProperty("detail").GetString()!);
            var receipt = launch.RootElement;
            // Authority capture can fail independently of a successful launch.
            // An incomplete diagnostic receipt cannot establish cleanup identity.
            if (!receipt.TryGetProperty("authority", out var authority) || authority.ValueKind != JsonValueKind.Object ||
                !receipt.TryGetProperty("nativeError", out var error) || error.ValueKind != JsonValueKind.Number ||
                !error.TryGetInt32(out var nativeError) || nativeError != 0 ||
                !authority.TryGetProperty("incumbentPid", out var ownerPid) || ownerPid.ValueKind != JsonValueKind.Number ||
                !ownerPid.TryGetInt32(out var incumbentPid) || incumbentPid != incumbent.ProcessId ||
                !authority.TryGetProperty("incumbentStartedAt", out var ownerStart) || ownerStart.ValueKind != JsonValueKind.String ||
                !ownerStart.TryGetDateTimeOffset(out var incumbentStartedAt) || incumbentStartedAt != incumbent.StartedAt ||
                !receipt.TryGetProperty("successorPid", out var childPid) || childPid.ValueKind != JsonValueKind.Number ||
                !childPid.TryGetInt32(out var successorPid) || successorPid <= 0 ||
                !receipt.TryGetProperty("successorStartedAt", out var childStart) || childStart.ValueKind != JsonValueKind.String ||
                !childStart.TryGetDateTimeOffset(out var successorStartedAt)) continue;
            return new(successorPid, successorStartedAt);
        }
        return null;
    }

    private static int CountTicks(string path) => File.Exists(path)
        ? ReadCompleteLiveLogLines(path).Count(line => line.StartsWith("TICK_END ", StringComparison.Ordinal) && line.Contains(" activation=true", StringComparison.Ordinal)) : 0;

    private static string ReadLiveLog(string path)
    {
        var read = new ProcessLogReader().ReadComplete(path);
        Assert.True(read.Content is not null, $"Live log read failed: {path}; reason={read.UnavailableReason}");
        return read.Content!;
    }

    private static IEnumerable<string> ReadCompleteLiveLogLines(string path)
    {
        // ReadComplete shares with writers/deletion. An unfinished line is revisited on the next bounded poll.
        var text = ReadLiveLog(path);
        var end = text.LastIndexOf('\n');
        return end < 0 ? [] : text[..end].Split('\n').Select(line => line.TrimEnd('\r'));
    }

    private static void AssertIdentity(Process process, ConductorSupervisorProcessIdentity identity)
    {
        Assert.False(process.HasExited);
        Assert.Equal(identity.StartedAt, new DateTimeOffset(process.StartTime.ToUniversalTime()));
    }

    private static bool LeaseMatches(string directory, ConductorSupervisorProcessIdentity identity)
    {
        try
        {
            using var lease = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "conduct-supervisor.lock")));
            var process = lease.RootElement.GetProperty("Process");
            return identity.ProcessId == process.GetProperty("ProcessId").GetInt32() &&
                identity.StartedAt == process.GetProperty("StartedAt").GetDateTimeOffset();
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string missingEvent)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(ObservationTimeout);
        try
        {
            while (!predicate()) await Task.Delay(50, timeout.Token);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(missingEvent, ex);
        }
    }
}
