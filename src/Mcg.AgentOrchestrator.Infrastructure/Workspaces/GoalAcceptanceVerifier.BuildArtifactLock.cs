using System.Text.Json;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using CommandResult = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.CommandResult;
using TransientBuildLockWaitResult = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.TransientBuildLockWaitResult;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GoalAcceptanceVerifierBuildArtifactLock
{
    internal static async Task<TransientBuildLockWaitResult> WaitForBuildArtifactWriteAccessAsync(
        string path,
        TimeSpan waitWindow,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        if (CanOpenBuildArtifactForWrite(path))
        {
            return new TransientBuildLockWaitResult((long)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, true);
        }

        var effectivePollInterval = pollInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(1)
            : pollInterval;
        while (timeProvider.GetElapsedTime(startedAt) < waitWindow)
        {
            var remaining = waitWindow - timeProvider.GetElapsedTime(startedAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(
                        remaining < effectivePollInterval ? remaining : effectivePollInterval,
                        timeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (CanOpenBuildArtifactForWrite(path))
            {
                return new TransientBuildLockWaitResult((long)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, true);
            }
        }

        return new TransientBuildLockWaitResult((long)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, false);
    }

    private static bool CanOpenBuildArtifactForWrite(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var probePath = Path.Combine(path, $".mcg-write-probe-{Guid.NewGuid():N}.tmp");
                using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                GoalAcceptanceVerifier.TryDeleteFile(probePath);
                return true;
            }

            if (File.Exists(path))
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                }

                return true;
            }

            var directory = Path.GetDirectoryName(path);
            return string.IsNullOrWhiteSpace(directory) ||
                !Directory.Exists(directory) ||
                CanOpenBuildArtifactForWrite(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void EmitTransientNoHolderBuildLockWaitReceipt(
        BuildLockAttribution attribution,
        TransientBuildLockWaitResult wait,
        int cycle,
        int maxCycles)
    {
        var probeMilliseconds = (long)(attribution.ProbeElapsed ?? TimeSpan.Zero).TotalMilliseconds;
        Console.WriteLine(
            $"LOCK_TRANSIENT_WAIT path={GoalAcceptanceVerifier.QuoteProgressToken(attribution.Path)} " +
            $"cycle={cycle.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"max-cycles={maxCycles.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"waited-ms={wait.WaitedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"probe-ms={probeMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"released={wait.Released.ToString().ToLowerInvariant()}");
        Console.Out.Flush();
    }

    internal static void EmitTransientNoHolderBuildLockRetryReceipt(
        AcceptanceManifestCheck check,
        BuildLockAttribution attribution,
        int cycle,
        int maxCycles,
        string verdict,
        int? exitCode,
        bool timedOut,
        bool buildLock)
    {
        var holder = attribution.Holders.FirstOrDefault();
        Console.WriteLine(
            $"LOCK_TRANSIENT_RETRY path={GoalAcceptanceVerifier.QuoteProgressToken(attribution.Path)} " +
            $"check={GoalAcceptanceVerifier.QuoteProgressToken(check.Name)} " +
            $"cycle={cycle.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"max-cycles={maxCycles.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"verdict={verdict} " +
            $"exit-code={(exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")} " +
            $"timed-out={timedOut.ToString().ToLowerInvariant()} " +
            $"build-lock={buildLock.ToString().ToLowerInvariant()} " +
            $"holder-pid={holder?.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} " +
            $"holder-name={GoalAcceptanceVerifier.QuoteProgressToken(holder?.ProcessName ?? "none")} " +
            $"attribution-source={GoalAcceptanceVerifier.QuoteProgressToken(attribution.Source)}");
        Console.Out.Flush();
    }

    internal static bool IsBuildArtifactIoException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    internal static BuildLockAttribution AttributeBuildLock(
        string path,
        string? ownershipHint,
        string? phase,
        string? operation,
        TimeProvider timeProvider)
    {
        var started = timeProvider.GetTimestamp();
        var attribution = LockAttribution.Attribute(path, ownershipHint, phase, operation);
        return attribution with { ProbeElapsed = timeProvider.GetElapsedTime(started) };
    }

    internal static bool IsBuildLockFailure(
        CommandResult result,
        DotnetBuildEnvironment environment,
        AcceptanceManifestCheck check,
        out BuildLockAttribution attribution,
        TimeProvider timeProvider,
        DotnetBuildStorageRoot storageRoot,
        GoalAcceptanceVerifier verifier)
    {
        attribution = null!;
        if (GoalAcceptanceVerifier.IsInterrupted(result) || result.ExitCode == 0 || GoalAcceptanceVerifier.DotnetTestRunReportsCompleted(result.Output))
        {
            return false;
        }

        if (!OutputDescribesBuildLock(result.Output))
        {
            return false;
        }

        var lockedPath = LockAttribution.TryExtractLockedPath(result.Output);
        if (lockedPath is null && !GoalAcceptanceVerifier.IsTransientCompilerLockFailure(result.Output))
        {
            return false;
        }

        attribution = AttributeBuildLock(
            lockedPath ?? environment.ArtifactsPath,
            environment.ArtifactsPath,
            "acceptance-output",
            "classify-build-lock",
            timeProvider);
        attribution = EnrichBuildLockAttributionWithGateContext(attribution, environment, check, storageRoot, verifier);
        EmitBuildLockClassificationContext(attribution, result, environment, check, storageRoot, verifier);
        return true;
    }

    private static bool OutputDescribesBuildLock(string output) =>
        output.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("file is locked", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("locked by another process", StringComparison.OrdinalIgnoreCase);

    private static BuildLockAttribution EnrichBuildLockAttributionWithGateContext(
        BuildLockAttribution attribution,
        DotnetBuildEnvironment environment,
        AcceptanceManifestCheck check,
        DotnetBuildStorageRoot storageRoot,
        GoalAcceptanceVerifier verifier)
    {
        var holders = attribution.Holders.ToList();
        var consumedGateContext = false;
        if (HasNoActionableHolder(attribution) &&
            DotnetBuildEnvironmentManager.TryFindActiveSlotArtifactConsumer(environment) is { } activeConsumer)
        {
            consumedGateContext = true;
            holders.Add(activeConsumer);
        }

        var heartbeat = ReadGateHeartbeat(environment, check, storageRoot, verifier);
        consumedGateContext |= heartbeat?.Snapshot is not null;
        foreach (var holder in GateHeartbeatLockHolderProjection.Build(heartbeat?.Snapshot, environment))
        {
            if (!holders.Any(existing => existing.ProcessId == holder.ProcessId))
            {
                holders.Add(holder);
            }
        }

        if (!consumedGateContext)
        {
            return attribution;
        }

        var enriched = attribution with
        {
            Holders = holders,
            Source = attribution.Source.Contains("+gate-context", StringComparison.Ordinal)
                ? attribution.Source
                : attribution.Source + "+gate-context"
        };
        LockAttribution.EmitReceipt(enriched);
        return enriched;
    }

    private static void EmitBuildLockClassificationContext(
        BuildLockAttribution attribution,
        CommandResult result,
        DotnetBuildEnvironment environment,
        AcceptanceManifestCheck check,
        DotnetBuildStorageRoot storageRoot,
        GoalAcceptanceVerifier verifier)
    {
        var heartbeat = ReadGateHeartbeat(environment, check, storageRoot, verifier);
        var snapshot = heartbeat?.Snapshot;
        var pidAlive = snapshot?.ProcessId is { } pid && GoalAcceptanceVerifier.IsProcessRunning(pid);
        var childAlive = snapshot?.ChildPid is { } childPid && GoalAcceptanceVerifier.IsProcessRunning(childPid);
        var line =
            $"LOCK_CONTEXT path={GoalAcceptanceVerifier.QuoteProgressToken(attribution.Path)} source={GoalAcceptanceVerifier.QuoteProgressToken(attribution.Source)} " +
            $"phase={GoalAcceptanceVerifier.QuoteProgressToken(attribution.Phase ?? "unknown")} operation={GoalAcceptanceVerifier.QuoteProgressToken(attribution.Operation ?? "unknown")} " +
            $"exit_code={result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"elapsed_ms={(long)(result.Elapsed ?? TimeSpan.Zero).TotalMilliseconds} " +
            $"stdout_bytes={result.StdoutBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"stderr_bytes={result.StderrBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"stdout={GoalAcceptanceVerifier.QuoteProgressToken(result.StdoutPath ?? "unknown")} stderr={GoalAcceptanceVerifier.QuoteProgressToken(result.StderrPath ?? "unknown")} " +
            $"heartbeat={GoalAcceptanceVerifier.QuoteProgressToken(heartbeat?.Path ?? Path.Combine(environment.ArtifactsPath, GateHeartbeatArtifacts.FileName))} " +
            $"heartbeat_available={(heartbeat?.IsAvailable == true).ToString().ToLowerInvariant()} " +
            $"heartbeat_state={GoalAcceptanceVerifier.QuoteProgressToken(snapshot?.State ?? heartbeat?.UnavailableReason ?? "unknown")} " +
            $"heartbeat_pid={snapshot?.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"heartbeat_pid_alive={pidAlive.ToString().ToLowerInvariant()} " +
            $"heartbeat_child_pid={snapshot?.ChildPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"heartbeat_child_alive={childAlive.ToString().ToLowerInvariant()} " +
            $"heartbeat_output_bytes={snapshot?.OutputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"slot_index={GoalAcceptanceVerifier.TryGetStableSlotIndex(environment)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}";
        Console.WriteLine(line);
        Console.Out.Flush();
    }

    private static GateHeartbeatStatus? ReadGateHeartbeat(
        DotnetBuildEnvironment environment,
        AcceptanceManifestCheck check,
        DotnetBuildStorageRoot storageRoot,
        GoalAcceptanceVerifier verifier)
    {
        var stableSlotIndex = GoalAcceptanceVerifier.TryGetStableSlotIndex(environment);
        var path = verifier.ResolveGateHeartbeatPath(
            check,
            environment,
            stableSlotIndex,
            worktreePath: null);
        if (!File.Exists(path))
        {
            return null;
        }

        if (stableSlotIndex.HasValue &&
            path.Equals(
                GateHeartbeatArtifacts.GetStableSlotPath(stableSlotIndex.Value, storageRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return GateHeartbeatArtifacts.ReadStableSlot(stableSlotIndex.Value, storageRoot);
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return snapshot is null
                ? new GateHeartbeatStatus(stableSlotIndex ?? -1, path, false, "invalid", null, null, null)
                : new GateHeartbeatStatus(
                    stableSlotIndex ?? -1,
                    path,
                    true,
                    null,
                    snapshot,
                    GoalAcceptanceVerifier.Positive(DateTimeOffset.UtcNow - snapshot.LastObservedAt),
                    GoalAcceptanceVerifier.Positive(DateTimeOffset.UtcNow - snapshot.LastProgressAt));
        }
        catch
        {
            return new GateHeartbeatStatus(
                stableSlotIndex ?? -1,
                path,
                false,
                "invalid",
                null,
                null,
                null);
        }
    }

    internal static bool IsTransientNoHolderBuildArtifactLock(BuildLockAttribution attribution, DotnetBuildEnvironment environment) =>
        HasNoActionableHolder(attribution) &&
        (GoalAcceptanceVerifier.PathIsUnderDirectory(attribution.Path, environment.ArtifactsPath) || IsBuildArtifactPath(attribution.Path));

    private static bool IsBuildArtifactPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".trx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lock", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNoActionableHolder(BuildLockAttribution attribution) =>
        attribution.Holders.Count == 0 ||
        attribution.Holders.All(holder =>
            holder.ProcessId is null &&
            (string.IsNullOrWhiteSpace(holder.ProcessName) ||
                holder.ProcessName.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                holder.ProcessName.Equals("unknown-probe-timeout", StringComparison.OrdinalIgnoreCase)));
}
