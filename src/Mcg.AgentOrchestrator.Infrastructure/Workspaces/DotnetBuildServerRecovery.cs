using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetBuildServerRecovery
{
    internal sealed record DotnetBuildServerShutdownOutcome(
        bool Exited,
        int? ExitCode,
        string StandardOutput,
        string StandardError,
        string? Failure = null);

    private static int s_heldExecutionLeaseCount;
    private static int s_compilerLockRecoveryRequested;
    private static readonly object s_buildServerRecoverySync = new();
    private static DotnetBuildServerShutdownOutcome? s_lastBuildServerShutdownOutcome;

    internal static Action? ShutdownBuildServersForTests { get; set; }
    internal static DotnetBuildServerShutdownOutcome? LastBuildServerShutdownOutcome =>
        Volatile.Read(ref s_lastBuildServerShutdownOutcome);

    public static void ShutdownBuildServersBestEffort()
    {
        if (ShutdownBuildServersForTests is { } shutdownForTests)
        {
            shutdownForTests();
            return;
        }

        try
        {
            RunBuildServerShutdownProcess(new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "build-server", "shutdown" }
            });
        }
        catch (Exception exception)
        {
            // Best effort cleanup only; build/test result handling owns the real verdict.
            Volatile.Write(
                ref s_lastBuildServerShutdownOutcome,
                new DotnetBuildServerShutdownOutcome(false, null, string.Empty, string.Empty, exception.Message));
        }
    }

    internal static DotnetBuildServerShutdownOutcome RunBuildServerShutdownProcess(
        ProcessStartInfo startInfo,
        int timeoutMilliseconds = 10_000)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            var notStarted = new DotnetBuildServerShutdownOutcome(
                false, null, string.Empty, string.Empty, "Process.Start returned null.");
            Volatile.Write(ref s_lastBuildServerShutdownOutcome, notStarted);
            return notStarted;
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(timeoutMilliseconds);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
            catch
            {
                // Best effort cleanup only; the retained outcome reports that the shutdown did not exit.
            }
        }

        Task.WaitAll([standardOutput, standardError], 2_000);
        var outcome = new DotnetBuildServerShutdownOutcome(
            exited,
            exited ? process.ExitCode : null,
            standardOutput.IsCompletedSuccessfully ? standardOutput.Result : string.Empty,
            standardError.IsCompletedSuccessfully ? standardError.Result : string.Empty);
        Volatile.Write(ref s_lastBuildServerShutdownOutcome, outcome);
        return outcome;
    }

    internal static bool ShutdownBuildServersForCompilerLockRecovery(bool requestRecovery = true)
    {
        lock (s_buildServerRecoverySync)
        {
            if (requestRecovery)
                s_compilerLockRecoveryRequested = 1;
            if (s_heldExecutionLeaseCount != 0 || s_compilerLockRecoveryRequested == 0)
                return false;

            s_compilerLockRecoveryRequested = 0;
            ShutdownBuildServersBestEffort();
            return true;
        }
    }

    internal static void RegisterExecutionLease()
    {
        lock (s_buildServerRecoverySync)
            s_heldExecutionLeaseCount++;
    }

    internal static void RequestCompilerLockRecovery()
    {
        lock (s_buildServerRecoverySync)
            s_compilerLockRecoveryRequested = 1;
    }

    internal static void ReleaseExecutionLease(bool attemptPendingRecovery)
    {
        lock (s_buildServerRecoverySync)
        {
            s_heldExecutionLeaseCount--;
            if (s_heldExecutionLeaseCount < 0)
            {
                s_heldExecutionLeaseCount = 0;
                throw new InvalidOperationException("The held dotnet build execution lease count became negative.");
            }

            if (attemptPendingRecovery && s_heldExecutionLeaseCount == 0)
                ShutdownBuildServersForCompilerLockRecovery(requestRecovery: false);
        }
    }
}
