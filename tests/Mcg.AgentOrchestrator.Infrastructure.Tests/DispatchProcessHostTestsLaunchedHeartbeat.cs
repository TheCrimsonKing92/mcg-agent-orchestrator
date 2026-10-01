using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Owns a release-file worker and serializes the static heartbeat publication hook.
[Xunit.Collection("ProcessSpawning")]
public sealed class DispatchProcessHostTestsLaunchedHeartbeat
{
    [Xunit.Fact]
    public async Task AttachedWorker_PublishesLaunchedIdentityBeforePeriodicHeartbeat()
    {
        var directory = NewDirectory();
        var releasePath = Path.Combine(directory, "release-worker");
        var parameters = Parameters(directory, releasePath);
        Task<int>? runTask = null;
        try
        {
            runTask = StartHost(parameters, new DispatchProcessHost.HeartbeatTestHooks());
            var heartbeat = await WaitForLaunchedHeartbeatAsync(parameters.HeartbeatPath!);
            Assert.Equal("launched", heartbeat.GetProperty("state").GetString());
            var childPid = heartbeat.GetProperty("childPid").GetInt32();
            var identity = Assert.Single(heartbeat.GetProperty("ownedProcessIdentities").EnumerateArray(),
                entry => entry.GetProperty("processId").GetInt32() == childPid);
            using var worker = Process.GetProcessById(childPid);
            Assert.False(worker.HasExited);
            Assert.Equal(worker.StartTime.ToUniversalTime(), identity.GetProperty("startedAt").GetDateTimeOffset().UtcDateTime);

            File.WriteAllText(releasePath, "release");
            Assert.Equal(0, await WaitForHostExitAsync(runTask));
            using var terminal = ReadHeartbeat(parameters.HeartbeatPath!);
            Assert.Equal("exited", terminal.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            await CleanupAsync(directory, releasePath, runTask);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task LaunchedWriteFailure_IsRecordedAndDispatchStillExitsNormally(bool payloadFailure)
    {
        var directory = NewDirectory();
        var releasePath = Path.Combine(directory, "release-worker");
        var parameters = Parameters(directory, releasePath);
        var failureRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payloadFailureInjected = 0;
        Task<int>? runTask = null;
        try
        {
            DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = (path, state) =>
            {
                if (!payloadFailure && path == parameters.HeartbeatPath && state == "launched")
                    throw new IOException("injected launched publication failure");
            };
            runTask = StartHost(parameters, new DispatchProcessHost.HeartbeatTestHooks
            {
                ObserveSelectedChild = childPid =>
                {
                    if (payloadFailure && childPid is not null &&
                        Interlocked.Exchange(ref payloadFailureInjected, 1) == 0)
                        throw new IOException("injected launched payload failure");
                },
                DiagnosticRecorded = diagnostic =>
                {
                    if (diagnostic.Contains("heartbeat write failed: state=launched", StringComparison.Ordinal))
                        failureRecorded.TrySetResult();
                }
            });
            try
            {
                await failureRecorded.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException failure)
            {
                throw new InvalidOperationException("The launched heartbeat write failure was not recorded.", failure);
            }

            File.WriteAllText(releasePath, "release");
            Assert.Equal(0, await WaitForHostExitAsync(runTask));
            Assert.True(DispatchExitArtifacts.TryRead(parameters.ExitCodePath, out var exitArtifact));
            Assert.Equal(0, exitArtifact.ExitCode);
            Assert.Equal(DispatchExitArtifactOrigin.Native, exitArtifact.Origin);
            var diagnostic = Assert.Single(File.ReadAllLines(parameters.HostDiagnosticPath!)
                .Where(line => line.Contains("heartbeat write failed: state=launched", StringComparison.Ordinal)));
            Assert.Contains("IOException", diagnostic, StringComparison.Ordinal);
            using var terminal = ReadHeartbeat(parameters.HeartbeatPath!);
            Assert.Equal("exited", terminal.RootElement.GetProperty("state").GetString());
            Assert.Contains("state=launched; IOException",
                terminal.RootElement.GetProperty("hostDiagnosticWriteFailure").GetString(), StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(directory, "heartbeat.json.*.tmp"));
        }
        finally
        {
            DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = null;
            await CleanupAsync(directory, releasePath, runTask);
        }
    }

    private static DispatchProcessHost.DispatchRunParameters Parameters(string directory, string releasePath) =>
        new($"while (!(Test-Path -LiteralPath '{releasePath.Replace("'", "''", StringComparison.Ordinal)}')) " +
            "{ [void][Math]::Sqrt(1234567) }; exit 0",
            directory, Path.Combine(directory, "out.log"), Path.Combine(directory, "err.log"),
            Path.Combine(directory, "exit.txt"), Path.Combine(directory, "heartbeat.json"),
            DisableSharedCompilation: false, Provider: WorkerSandboxProvider.Claude,
            HostDiagnosticPath: Path.Combine(directory, "host.err.log"), HeartbeatIntervalMilliseconds: 3_600_000);

    private static Task<int> StartHost(
        DispatchProcessHost.DispatchRunParameters parameters, DispatchProcessHost.HeartbeatTestHooks hooks)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(DispatchProcessHost.RunWithHeartbeatHooksForTests(parameters, hooks)); }
            catch (Exception failure) { completion.TrySetException(failure); }
        }) { IsBackground = true };
        thread.Start();
        return completion.Task;
    }

    private static async Task<JsonElement> WaitForLaunchedHeartbeatAsync(string heartbeatPath)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        void TryRead()
        {
            try
            {
                using var heartbeat = ReadHeartbeat(heartbeatPath);
                if (heartbeat.RootElement.GetProperty("state").GetString() == "launched")
                    completion.TrySetResult(heartbeat.RootElement.Clone());
            }
            catch (Exception failure) when (failure is IOException or JsonException)
            {
                // Atomic replacement may race a notification; the next notification retries.
            }
        }

        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(heartbeatPath)!, Path.GetFileName(heartbeatPath) + "*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        watcher.Created += (_, _) => TryRead();
        watcher.Changed += (_, _) => TryRead();
        watcher.Renamed += (_, _) => TryRead();
        watcher.EnableRaisingEvents = true;
        TryRead();
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException failure)
        {
            TryRead();
            if (completion.Task.IsCompletedSuccessfully) return completion.Task.Result;
            throw new InvalidOperationException("The launched heartbeat was not published while the worker was held.", failure);
        }
    }

    private static JsonDocument ReadHeartbeat(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(stream);
    }

    private static async Task<int> WaitForHostExitAsync(Task<int> runTask)
    {
        try { return await runTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); }
        catch (TimeoutException failure)
        {
            throw new InvalidOperationException("The dispatch host did not exit after the worker was released.", failure);
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-launched-heartbeat", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task CleanupAsync(string directory, string releasePath, Task<int>? runTask)
    {
        File.WriteAllText(releasePath, "release");
        if (runTask is not null)
        {
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(60)); }
            catch when (runTask.IsCompleted) { }
        }
        Directory.Delete(directory, recursive: true);
    }
}
