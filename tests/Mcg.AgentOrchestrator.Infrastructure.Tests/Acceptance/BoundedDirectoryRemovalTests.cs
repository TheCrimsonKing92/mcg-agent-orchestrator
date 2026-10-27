using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests;

// Parallel-safe: each case owns its directory and exact child handle, with stdin/stdout gates.
public sealed class BoundedDirectoryRemovalTests
{
    [Fact]
    public async Task HeldFile_ReleasedAfterFirstFailure_RemovesDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var holder = new FileHolder();
        await holder.WaitForHeldAsync();
        var attempts = new List<int>();

        await Task.Run(() => BoundedDirectoryRemoval.Remove(holder.Root, [holder.Process], (attempt, _) =>
        {
            attempts.Add(attempt);
            // Block retries until the child confirms its handle is closed, regardless of host speed.
            if (attempt == 1) holder.ReleaseAsync().GetAwaiter().GetResult();
        })).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotEmpty(attempts);
        Assert.Equal(1, attempts[0]);
        Assert.False(Directory.Exists(holder.Root));
    }

    [Fact]
    public async Task HeldFile_NeverReleased_ExhaustsAttemptsAndNamesRunningChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var holder = new FileHolder();
        await holder.WaitForHeldAsync();
        var attempts = new List<int>();

        var failure = Assert.Throws<IOException>(() => BoundedDirectoryRemoval.Remove(
            holder.Root, [holder.Process], (attempt, _) => attempts.Add(attempt)));

        Assert.Equal(Enumerable.Range(1, 8), attempts);
        Assert.Equal(32, Assert.IsType<IOException>(failure.InnerException).HResult & 0xFFFF);
        Assert.Contains(Path.GetFullPath(holder.Root), failure.Message);
        Assert.Contains($"pid={holder.Process.Id} name=powershell status=running", failure.Message);
        Assert.False(holder.Process.HasExited);
        Assert.True(Directory.Exists(holder.Root));
    }

    private sealed class FileHolder : IAsyncDisposable
    {
        private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
        private readonly Task<string> _error;
        private bool _released;
        internal string Root { get; } = Directory.CreateTempSubdirectory("bounded-removal-").FullName;
        internal Process Process { get; }

        internal FileHolder()
        {
            Process? child = null;
            try
            {
                var path = Path.Combine(Root, "held.txt");
                File.WriteAllText(path, "held by the child until release");
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var start = new ProcessStartInfo(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = system,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.Environment.Clear();
                foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
                    if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
                var script = "$ErrorActionPreference='Stop';" +
                    $"$file=[IO.File]::Open('{path.Replace("'", "''")}', 'Open', 'ReadWrite', 'None');" +
                    "try {[Console]::WriteLine('held');" +
                    "if ([Console]::ReadLine() -ne 'release') {throw 'Expected release command'}}" +
                    "finally {$file.Dispose()};[Console]::WriteLine('released')";
                foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
                Process = child = ProcessTreeGuiSuppression.Start(start);
                _ = Process.SafeHandle; // Retain the exact child identity through exit and cleanup.
                _error = Process.StandardError.ReadToEndAsync();
            }
            catch
            {
                try
                {
                    if (child is not null)
                    {
                        if (!child.HasExited) child.Kill(entireProcessTree: true);
                        Assert.True(child.WaitForExit(30_000), "File holder did not exit after failed startup.");
                    }
                    BoundedDirectoryRemoval.Remove(Root, []);
                }
                finally { child?.Dispose(); }
                throw;
            }
        }

        internal Task WaitForHeldAsync() => ExpectSignalAsync("held");

        internal async Task ReleaseAsync()
        {
            await Process.StandardInput.WriteLineAsync("release");
            await Process.StandardInput.FlushAsync();
            await ExpectSignalAsync("released");
            _released = true;
        }

        private async Task ExpectSignalAsync(string signal)
        {
            string? line;
            try { line = await Process.StandardOutput.ReadLineAsync().WaitAsync(HangGuard); }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"File holder did not acknowledge '{signal}'.", ex);
            }
            Assert.Equal(signal, line);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_released && !Process.HasExited) await ReleaseAsync();
                await Process.WaitForExitAsync().WaitAsync(HangGuard);
                Assert.Equal(string.Empty, await _error.WaitAsync(HangGuard));
                Assert.Equal(0, Process.ExitCode);
            }
            finally
            {
                try
                {
                    if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().WaitAsync(HangGuard);
                    BoundedDirectoryRemoval.Remove(Root, [Process]);
                }
                finally { Process.Dispose(); }
            }
        }
    }
}
