using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;

internal static class OwnedAttemptExitWait
{
    internal sealed record Observation(
        long TrailLength, long StdoutLength, long StderrLength, DateTime? HeartbeatWriteUtc,
        string Trail, string Heartbeat, string Stdout, string Stderr)
    {
        internal (long, long, long, DateTime?) Progress =>
            (TrailLength, StdoutLength, StderrLength, HeartbeatWriteUtc);
    }

    internal static void WaitForExit(
        int processId, Func<bool> hasExited, Func<Observation> observe,
        Func<TimeSpan> now, Action<TimeSpan> sleep)
    {
        var started = now();
        if (hasExited()) return;
        var previous = observe();
        var lastProgress = started;
        var window = TestHangGuard.HangSafetyBound;
        var failsafe = TimeSpan.FromMilliseconds(TestHangGuard.WholeTestMilliseconds);
        while (true)
        {
            var elapsed = now();
            var remaining = TimeSpan.FromTicks(Math.Min(
                (started + failsafe - elapsed).Ticks, (lastProgress + window - elapsed).Ticks));
            if (remaining > TimeSpan.Zero)
                sleep(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
            if (hasExited()) return;
            var current = observe();
            elapsed = now();
            if (current.Progress != previous.Progress) lastProgress = elapsed;
            previous = current;
            var reason = elapsed - started >= failsafe
                ? $"did not exit within the {failsafe.TotalSeconds:0} s whole-test failsafe"
                : elapsed - lastProgress >= window ? $"made no progress for {window.TotalSeconds:0} s" : null;
            if (reason is not null)
                Assert.Fail($"External acceptance PID {processId} {reason}. "
                    + $"lastPhase={AcceptanceAttemptOwnedPhaseTrail.LastPhaseIn(current.Trail) ?? "none"}"
                    + $"\ntrail={current.Trail}\nheartbeat={current.Heartbeat}\nstdout={current.Stdout}\nstderr={current.Stderr}");
        }
    }

    internal static void WaitForExit(Process process, ConductorParallelAcceptanceAttempt attempt)
    {
        var clock = Stopwatch.StartNew();
        var trail = MissingFile;
        var stdout = MissingFile;
        var stderr = MissingFile;
        var heartbeat = MissingFile;
        Observation Observe()
        {
            trail = ReadFile(AcceptanceAttemptOwnedPhaseTrail.PathFor(attempt.MetadataPath), trail);
            stdout = ReadFile(attempt.StdoutPath, stdout);
            stderr = ReadFile(attempt.StderrPath, stderr);
            heartbeat = ReadFile(attempt.HeartbeatPath, heartbeat);
            return new(trail.Length, stdout.Length, stderr.Length, heartbeat.WriteUtc,
                trail.Text, heartbeat.Text, stdout.Text, stderr.Text);
        }
        WaitForExit(process.Id, () => process.WaitForExit(0), Observe, () => clock.Elapsed, Thread.Sleep);
        process.WaitForExit(); // Drain the existing asynchronous stdout/stderr readers.
    }

    private static readonly (long Length, DateTime? WriteUtc, string Text) MissingFile = (-1, null, "<missing>");

    private static (long Length, DateTime? WriteUtc, string Text) ReadFile(
        string path, (long Length, DateTime? WriteUtc, string Text) previous)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var length = stream.Length;
            var writeUtc = File.GetLastWriteTimeUtc(path);
            return (length, writeUtc, reader.ReadToEnd());
        }
        catch (FileNotFoundException) { return MissingFile; }
        catch (DirectoryNotFoundException) { return MissingFile; }
        catch (IOException ex) { return (previous.Length, previous.WriteUtc, $"<read-error: {ex.GetType().Name}>"); }
        catch (UnauthorizedAccessException ex) { return (previous.Length, previous.WriteUtc, $"<read-error: {ex.GetType().Name}>"); }
    }
}
