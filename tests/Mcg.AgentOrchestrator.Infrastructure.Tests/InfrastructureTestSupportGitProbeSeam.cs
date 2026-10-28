using System.Diagnostics;
using System.Threading;

internal static partial class InfrastructureTestSupport
{
    private sealed record GitProbeSeam(
        IReadOnlyDictionary<string, string?>? InheritedEnvironment,
        Func<Process, bool>? StartProcess,
        Func<Func<bool>, bool>? WaitForExit,
        Func<Func<int?>, int?>? ReadExitCode,
        Func<Func<bool>, bool>? WaitForDrain,
        Func<Stream, Stream>? CaptureStream);

    private static readonly AsyncLocal<GitProbeSeam?> GitProbeSeamScope = new();
    private static readonly AsyncLocal<GitProbeLaunchCounter?> GitProbeLaunchCounterScope = new();

    internal static GitProbeLaunchCounter CountGitProbeLaunches()
    {
        var counter = new GitProbeLaunchCounter(GitProbeLaunchCounterScope.Value);
        GitProbeLaunchCounterScope.Value = counter;
        return counter;
    }

    private static void RecordGitProbeLaunch() => GitProbeLaunchCounterScope.Value?.Increment();

    internal sealed class GitProbeLaunchCounter(GitProbeLaunchCounter? previous) : IDisposable
    {
        private int count;
        private bool disposed;

        internal int Count => Volatile.Read(ref count);

        internal void Increment() => Interlocked.Increment(ref count);

        public void Dispose()
        {
            if (disposed)
                return;

            GitProbeLaunchCounterScope.Value = previous;
            disposed = true;
        }
    }

    internal static IDisposable UseGitProbeSeam(
        IReadOnlyDictionary<string, string?>? inheritedEnvironment = null,
        Func<Process, bool>? startProcess = null,
        Func<Func<bool>, bool>? waitForExit = null,
        Func<Func<int?>, int?>? readExitCode = null,
        Func<Func<bool>, bool>? waitForDrain = null,
        Func<Stream, Stream>? captureStream = null)
    {
        var previous = GitProbeSeamScope.Value;
        GitProbeSeamScope.Value = new GitProbeSeam(
            inheritedEnvironment, startProcess, waitForExit, readExitCode, waitForDrain, captureStream);
        return new RestoreGitProbeSeam(previous);
    }

    internal static void RequireCompleteGitOutput(WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult result)
    {
        if (result.StandardOutputTruncated)
            throw new InvalidOperationException($"git output truncated: {result}");
    }

    private sealed class RestoreGitProbeSeam(GitProbeSeam? previous) : IDisposable
    {
        public void Dispose() => GitProbeSeamScope.Value = previous;
    }

    // Mutable only within one probe. Snapshots on GitProbeResult are immutable and invocation-local.
    private sealed class GitProbeObservationRecorder(GitProbeSeam? seam)
    {
        private WorkerDispatchTestsSeededRepositoryFactory.GitProbeOperationObservation wait = new();
        private WorkerDispatchTestsSeededRepositoryFactory.GitProbeOperationObservation cleanupWait = new();
        private WorkerDispatchTestsSeededRepositoryFactory.GitProbeOperationObservation exit = new();

        internal WorkerDispatchTestsSeededRepositoryFactory.GitProbeObservations Snapshot =>
            new(wait, cleanupWait, exit);

        internal bool? Wait(Func<bool> operation, bool cleanup = false,
            Func<Exception, bool>? isDeadlineCancellation = null)
        {
            var observation = new WorkerDispatchTestsSeededRepositoryFactory.GitProbeOperationObservation(Attempted: true);
            if (cleanup) cleanupWait = observation; else wait = observation;
            try
            {
                // Cleanup waiting uses the actual operation, not the primary-wait override.
                var exited = cleanup ? operation() : (seam?.WaitForExit ?? (run => run()))(operation);
                observation = observation with { Completed = true, Exited = exited };
                return exited;
            }
            catch (Exception ex)
            {
                observation = observation.WithException(ex);
                return isDeadlineCancellation?.Invoke(ex) == true ? false : null;
            }
            finally
            {
                if (cleanup) cleanupWait = observation; else wait = observation;
            }
        }

        internal int? ReadExitCode(Func<int?> operation)
        {
            exit = new(Attempted: true);
            try
            {
                var code = (seam?.ReadExitCode ?? (run => run()))(operation);
                exit = exit with { Completed = true };
                return code;
            }
            catch (Exception ex)
            {
                exit = exit.WithException(ex);
                return null;
            }
        }
    }
}
