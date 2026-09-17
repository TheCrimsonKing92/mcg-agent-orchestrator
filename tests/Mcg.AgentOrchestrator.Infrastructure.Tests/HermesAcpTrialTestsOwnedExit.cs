using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class HermesAcpTrialTestsOwnedExit
{
    [Fact]
    public async Task RootExitWaitRemainsCancellableUntilOwnedChildExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "mcg-hermes-owned-exit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ready = Path.Combine(directory, "ready");
        var release = Path.Combine(directory, "release");
        var childScript = Path.Combine(directory, "child.ps1");
        static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        File.WriteAllText(childScript,
            $"[IO.File]::WriteAllText({Quote(ready)}, [string]$PID); while (!(Test-Path -LiteralPath {Quote(release)})) {{ Start-Sleep -Milliseconds 10 }}; exit 0");
        var startInfo = new ProcessStartInfo(WorkerShell.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"$child = Start-Process -FilePath {Quote(WorkerShell.Executable)} -ArgumentList @('-NoProfile', '-NonInteractive', '-File', {Quote("\"" + childScript + "\"")}) -WindowStyle Hidden -PassThru; while (!(Test-Path -LiteralPath {Quote(ready)})) {{ Start-Sleep -Milliseconds 10 }}; exit 0" })
            startInfo.ArgumentList.Add(argument);
        using var process = new HermesAcpProcessLauncher().Start(startInfo);
        Exception? testFailure = null;
        try
        {
            process.CompleteInput();
            using var rootDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(rootDeadline.Token);
            Assert.True(File.Exists(ready), "Root exited before its owned child became ready.");
            Assert.Equal(0, process.ExitCode);
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(ready), System.Globalization.CultureInfo.InvariantCulture));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WaitForOwnedExitAsync(deadline.Token));
            Assert.False(child.HasExited, "Cancelling the wait must not terminate the child.");
            Assert.False(process.JobExitConfirmed, "A live child must prevent whole-job exit confirmation.");
            File.WriteAllText(release, "release");
            using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForOwnedExitAsync(drainDeadline.Token);
            Assert.True(process.JobExitConfirmed, process.DescribeJobExitObservation());
            Assert.True(process.SurvivorInventoryEmpty);
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                File.WriteAllText(release, "release");
                if (!process.JobExitConfirmed) process.Kill();
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForOwnedExitAsync(cleanupDeadline.Token);
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception cleanupFailure) when (testFailure is not null)
            {
                throw new AggregateException("Hermes control and cleanup both failed.", testFailure, cleanupFailure);
            }
        }
    }
}
